namespace Routeables.Remote;

/// <summary>What the server knows of one replica: its owner, the last change it decided on, and its last answers.</summary>
/// <param name="Replica">The replica.</param>
/// <param name="User">The user it belongs to: the first that used it.</param>
/// <param name="LastSeq">The last seq decided on, accepted or refused.</param>
/// <param name="Acks">The last answers, oldest first, to answer a push sent again after a lost answer.</param>
public sealed record ReplicaRecord(ReplicaId Replica, string User, long LastSeq, IReadOnlyList<ChangeAck> Acks);

/// <summary>Where the log is.</summary>
/// <param name="Position">The last position given (0 for none).</param>
/// <param name="Horizon">The positions up to it are gone from the log (compacted): a client behind it needs a snapshot.</param>
public readonly record struct ReplicationHead(long Position, long Horizon);

/// <summary>
/// What one decision writes, at once: the replica (its new <see cref="ReplicaRecord.LastSeq"/> and answer), and, when
/// the change was accepted, its entry in the log and the documents it changed. A store writes the three in one
/// transaction: a crash never leaves a change in the log without its documents, nor a seq used without its answer.
/// </summary>
public sealed record ReplicationCommit(ReplicaRecord Replica, LogEntry? Entry, IReadOnlyList<DocumentState> Documents);

/// <summary>
/// Where a server keeps the log, the merged documents and the replicas: in memory (<see cref="MemoryReplicationStore"/>)
/// or in a database (Queryeables.Replication, over any IEntityStore: EF Core, MongoDB, Dommel...).
/// </summary>
/// <remarks>
/// One server writes at a time (<see cref="ReplicationServer"/> serializes its decisions): a store needs no locking of
/// its own beyond the atomicity of <see cref="CommitAsync"/>. Several server processes over one database need a lock in
/// the database around the decisions; version 1 does not provide it.
/// </remarks>
public interface IReplicationStore
{
    ValueTask<ReplicationHead> HeadAsync(CancellationToken ct = default);

    ValueTask<ReplicaRecord?> FindReplicaAsync(ReplicaId replica, CancellationToken ct = default);

    /// <summary>The merged state of a document, or null. The caller may change what it gets: a store returns a copy.</summary>
    ValueTask<DocumentState?> FindAsync(DocumentId id, CancellationToken ct = default);

    /// <summary>Every document of the collections.</summary>
    ValueTask<IReadOnlyList<DocumentState>> DocumentsAsync(IReadOnlyCollection<string> collections, CancellationToken ct = default);

    /// <summary>
    /// The entries after <paramref name="after"/> and up to <paramref name="upTo"/> with an operation on one of
    /// <paramref name="collections"/>, by position, at most <paramref name="max"/>.
    /// </summary>
    ValueTask<IReadOnlyList<LogEntry>> ReadAsync(long after, long upTo, IReadOnlyCollection<string> collections, int max, CancellationToken ct = default);

    /// <summary>Writes one decision, all of it or nothing.</summary>
    ValueTask CommitAsync(ReplicationCommit commit, CancellationToken ct = default);

    /// <summary>Removes the entries up to <paramref name="upTo"/> and moves the horizon there.</summary>
    ValueTask CompactAsync(long upTo, CancellationToken ct = default);
}

/// <summary>A store in memory: tests, and a server whose clients may all start again from a snapshot after a restart.</summary>
public sealed class MemoryReplicationStore : IReplicationStore
{
    private readonly object _gate = new();
    private readonly List<LogEntry> _log = [];
    private readonly Dictionary<DocumentId, DocumentState> _documents = [];
    private readonly Dictionary<ReplicaId, ReplicaRecord> _replicas = [];
    private long _position;
    private long _horizon;

    /// <summary>The entries still in the log.</summary>
    public IReadOnlyList<LogEntry> Log
    {
        get
        {
            lock (_gate)
                return [.. _log];
        }
    }

    public ValueTask<ReplicationHead> HeadAsync(CancellationToken ct = default)
    {
        lock (_gate)
            return ValueTask.FromResult(new ReplicationHead(_position, _horizon));
    }

    public ValueTask<ReplicaRecord?> FindReplicaAsync(ReplicaId replica, CancellationToken ct = default)
    {
        lock (_gate)
            return ValueTask.FromResult(_replicas.GetValueOrDefault(replica));
    }

    public ValueTask<DocumentState?> FindAsync(DocumentId id, CancellationToken ct = default)
    {
        lock (_gate)
            return ValueTask.FromResult(_documents.GetValueOrDefault(id)?.Clone());
    }

    public ValueTask<IReadOnlyList<DocumentState>> DocumentsAsync(IReadOnlyCollection<string> collections, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<DocumentState> documents = [.. _documents.Values.Where(d => collections.Contains(d.Id.Collection)).Select(d => d.Clone())];
            return ValueTask.FromResult(documents);
        }
    }

    public ValueTask<IReadOnlyList<LogEntry>> ReadAsync(long after, long upTo, IReadOnlyCollection<string> collections, int max, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<LogEntry> entries = [.. _log
                .Where(e => e.Position > after && e.Position <= upTo && e.Change.Ops.Any(o => collections.Contains(o.Document.Collection)))
                .Take(max)];
            return ValueTask.FromResult(entries);
        }
    }

    public ValueTask CommitAsync(ReplicationCommit commit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        lock (_gate)
        {
            if (commit.Entry is { } entry)
            {
                if (entry.Position != _position + 1)
                    throw new InvalidOperationException($"The next position is {_position + 1}, not {entry.Position}.");
                _log.Add(entry);
                _position = entry.Position;
            }
            foreach (var document in commit.Documents)
                _documents[document.Id] = document.Clone();
            _replicas[commit.Replica.Replica] = commit.Replica;
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask CompactAsync(long upTo, CancellationToken ct = default)
    {
        lock (_gate)
        {
            upTo = Math.Min(upTo, _position);
            _log.RemoveAll(e => e.Position <= upTo);
            _horizon = Math.Max(_horizon, upTo);
        }
        return ValueTask.CompletedTask;
    }
}
