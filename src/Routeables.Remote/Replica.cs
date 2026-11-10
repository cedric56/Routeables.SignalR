namespace Routeables.Remote;

/// <summary>A change the server refused: kept for the application to show, correct or drop; its effect is gone.</summary>
public sealed record RejectedChange(Change Change, string? Code, string? Message);

/// <summary>Where a replica keeps its state between two runs: one value, written whole (see <see cref="ReplicaCodec"/>).</summary>
public interface IReplicaStorage
{
    ValueTask<byte[]?> ReadAsync(CancellationToken ct = default);

    ValueTask WriteAsync(byte[] state, CancellationToken ct = default);
}

/// <summary>A storage in memory: tests, and replicas that need not survive a restart.</summary>
public sealed class MemoryReplicaStorage : IReplicaStorage
{
    private byte[]? _state;

    public ValueTask<byte[]?> ReadAsync(CancellationToken ct = default) => ValueTask.FromResult(_state?.ToArray());

    public ValueTask WriteAsync(byte[] state, CancellationToken ct = default)
    {
        _state = state.ToArray();
        return ValueTask.CompletedTask;
    }
}

/// <summary>The outcome of a synchronization.</summary>
/// <param name="Pushed">Changes the server decided on.</param>
/// <param name="Rejected">Of them, refused.</param>
/// <param name="Pulled">Changes received.</param>
/// <param name="Snapshot">Whether the documents were replaced by a snapshot (the cursor was too old).</param>
public sealed record SyncReport(int Pushed, int Rejected, int Pulled, bool Snapshot);

/// <summary>
/// The client side of the protocol: a replica that writes offline at once, and converges with the server when it
/// synchronizes.
/// <list type="bullet">
/// <item><b>Confirmed</b>: the documents as the server's log made them, from what was pulled.</item>
/// <item><b>Pending</b>: the changes of this replica not yet seen in a pull, in their order.</item>
/// <item><b>What the application reads</b> (<see cref="Find"/>): confirmed, then pending applied over it. A refused change
/// leaves pending, and its effect is gone exactly: nothing has to be undone.</item>
/// </list>
/// Every change is stored before it is sent; a synchronization can stop anywhere (the connection drops, the process
/// stops) and start again: pushes are recognized by their dot, pulls start from the cursor.
/// </summary>
public sealed class Replica
{
    private readonly object _gate = new();
    private readonly IReplicaStorage _storage;
    private readonly DocumentSet _confirmed = new();
    private readonly List<Pending> _pending = [];
    private readonly List<RejectedChange> _rejected = [];
    private readonly HashSet<string> _collections = new(StringComparer.Ordinal);
    private long _nextSeq = 1;
    private long _cursor;

    private Replica(ReplicaId id, IReplicaStorage storage, TimeProvider? time, Hlc? clock)
    {
        Id = id;
        _storage = storage;
        Clock = new HybridClock(id, time, clock);
    }

    public ReplicaId Id { get; }

    public HybridClock Clock { get; }

    /// <summary>The last position of the server's log this replica received.</summary>
    public long Cursor
    {
        get
        {
            lock (_gate)
                return _cursor;
        }
    }

    /// <summary>Changes made here and not yet confirmed by a pull: sent or not.</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
                return _pending.Count;
        }
    }

    /// <summary>Changes the server refused.</summary>
    public IReadOnlyList<RejectedChange> Rejected
    {
        get
        {
            lock (_gate)
                return [.. _rejected];
        }
    }

    public IReadOnlyCollection<string> Collections
    {
        get
        {
            lock (_gate)
                return [.. _collections];
        }
    }

    /// <summary>Raised after local changes and after a synchronization, with the documents that may have changed.</summary>
    public event Action<IReadOnlyCollection<DocumentId>>? Changed;

    /// <summary>
    /// Opens the replica stored in <paramref name="storage"/>, or a new one replicating <paramref name="collections"/>. A
    /// replica opened with other collections than those it stored asks a snapshot at its next synchronization.
    /// </summary>
    public static async Task<Replica> OpenAsync(IReplicaStorage storage, IEnumerable<string> collections, TimeProvider? time = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(collections);
        var wanted = collections.ToHashSet(StringComparer.Ordinal);
        var stored = ReplicaCodec.Read(await storage.ReadAsync(ct));
        Replica replica;
        if (stored is null)
        {
            replica = new Replica(ReplicaId.New(), storage, time, null);
        }
        else
        {
            replica = new Replica(stored.Id, storage, time, stored.Clock);
            replica._confirmed.Load(stored.Documents, stored.Applied);
            replica._pending.AddRange(stored.Pending.Select(p => new Pending(p.Change, p.Position)));
            replica._rejected.AddRange(stored.Rejected);
            replica._nextSeq = stored.NextSeq;
            replica._cursor = wanted.SetEquals(stored.Collections) ? stored.Cursor : -1;   // other collections: a snapshot
        }
        replica._collections.UnionWith(wanted);
        await replica.SaveAsync(ct);
        return replica;
    }

    /// <summary>The document as the application sees it: confirmed by the server, with the pending changes of this replica over it.</summary>
    public DocumentState? Find(DocumentId id)
    {
        lock (_gate)
            return View(id);
    }

    /// <summary>The documents of a collection the application sees, deleted ones excepted.</summary>
    public IReadOnlyList<DocumentState> All(string collection)
    {
        lock (_gate)
        {
            var ids = _confirmed.Documents.Select(d => d.Id)
                .Concat(_pending.SelectMany(p => p.Change.Ops.Select(o => o.Document)))
                .Where(id => id.Collection == collection)
                .Distinct();
            return [.. ids.Select(View).Where(d => d is { Exists: true })!];
        }
    }

    private DocumentState? View(DocumentId id)
    {
        var state = _confirmed.Find(id)?.Clone();
        foreach (var pending in _pending)
        {
            var ops = pending.Change.Ops;
            for (var index = 0; index < ops.Count; index++)
            {
                if (ops[index].Document != id)
                    continue;
                state ??= new DocumentState(id);
                state.Apply(ops[index].Op, new OpStamp(pending.Change.Stamp, index), pending.Change.Dot);
            }
        }
        return state;
    }

    /// <summary>Starts a change: operations collected, then <see cref="ChangeBuilder.CommitAsync"/> stores them as one change.</summary>
    public ChangeBuilder Edit() => new(this);

    internal async Task<Change> CommitAsync(IReadOnlyList<(DocumentId Document, Func<Func<DocumentId, DocumentState?>, IEnumerable<FieldOp>> Build)> ops,
        CancellationToken ct)
    {
        Change change;
        lock (_gate)
        {
            // Operations that depend on what the replica sees (a removal observes the additions, an insertion its anchor)
            // are built now, under the lock, each one seeing the operations of this change before it.
            var dot = new Dot(Id, _nextSeq);
            var stamp = Clock.Next();
            var working = new Dictionary<DocumentId, DocumentState?>();
            DocumentState? Current(DocumentId id)
            {
                if (!working.TryGetValue(id, out var state))
                    working[id] = state = View(id);
                return state;
            }
            var built = new List<DocumentOp>(ops.Count);
            foreach (var (document, build) in ops)
            {
                foreach (var op in build(Current).ToList())
                {
                    var state = Current(document) ?? (working[document] = new DocumentState(document));
                    state.Apply(op, new OpStamp(stamp, built.Count), dot);
                    built.Add(new DocumentOp(document, op));
                }
            }
            if (built.Count == 0)
                throw new InvalidOperationException("The change does nothing.");
            _nextSeq++;
            change = new Change(dot, stamp, built);
            _pending.Add(new Pending(change, 0));
        }
        await SaveAsync(ct);
        Changed?.Invoke([.. change.Ops.Select(o => o.Document).Distinct()]);
        return change;
    }

    /// <summary>
    /// Sends the pending changes, then receives what the server has after the cursor (or a snapshot when the cursor is too
    /// old). Stops at the first failure of the channel, with everything stored: call it again.
    /// </summary>
    public async Task<SyncReport> SyncAsync(IReplicationChannel channel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var welcome = await channel.HelloAsync(new RemoteHello(RemoteProtocol.Version, Id, Clock.Last), ct);
        if (!welcome.Success)
            throw new InvalidOperationException(welcome.Error ?? "The server refused the replica.");
        Clock.Observe(welcome.Clock);

        var (pushed, rejected) = await PushAsync(channel, welcome, ct);
        var (pulled, snapshot) = await PullAsync(channel, ct);
        return new SyncReport(pushed, rejected, pulled, snapshot);
    }

    private async Task<(int Pushed, int Rejected)> PushAsync(IReplicationChannel channel, RemoteWelcome welcome, CancellationToken ct)
    {
        int pushed = 0, refused = 0;
        var fellBack = false;
        while (true)
        {
            List<Change> batch;
            lock (_gate)
            {
                // Sent from what the server has not decided yet; what it decided and this replica did not hear is answered again.
                batch = [.. _pending.Where(p => p.Position == 0).Select(p => p.Change).Take(Math.Max(1, welcome.MaxChangesPerPush))];
            }
            if (batch.Count == 0)
                return (pushed, refused);

            var result = await channel.PushAsync(new PushRequest(batch), ct);
            var restamp = new List<Change>();
            lock (_gate)
            {
                foreach (var ack in result.Acks)
                {
                    var index = _pending.FindIndex(p => p.Change.Dot == ack.Dot);
                    if (index < 0)
                        continue;
                    pushed++;
                    switch (ack.Status)
                    {
                        case AckStatus.Accepted or AckStatus.Duplicate:
                            // Accepted: it stays pending (and seen) until a pull brings it back from the log, at that position.
                            _pending[index] = _pending[index] with { Position = ack.Position > 0 ? ack.Position : long.MaxValue };
                            break;
                        case AckStatus.Rejected when ack.Code == RemoteErrorCodes.ClockSkew:
                            // Not a decision on the intent: the same operations again, stamped by a corrected clock.
                            restamp.Add(_pending[index].Change);
                            _pending.RemoveAt(index);
                            break;
                        case AckStatus.Rejected:
                            refused++;
                            _rejected.Add(new RejectedChange(_pending[index].Change, ack.Code, ack.Message));
                            _pending.RemoveAt(index);
                            break;
                    }
                }
                if (restamp.Count > 0)
                    Clock.FallBack(welcome.Clock);
                // The same intent, a new dot (the server used up the old one), in the same order. A later removal that
                // observed an element added by the old dot now observes it under the new one.
                var renamed = new Dictionary<Dot, Dot>();
                foreach (var change in restamp)
                {
                    var dot = new Dot(Id, _nextSeq++);
                    renamed[change.Dot] = dot;
                    _pending.Add(new Pending(change with { Dot = dot, Stamp = Clock.Next() }, 0));
                }
                for (var i = 0; i < _pending.Count && renamed.Count > 0; i++)
                    _pending[i] = _pending[i] with { Change = Rename(_pending[i].Change, renamed) };
            }
            await SaveAsync(ct);
            if (result.Acks.Count < batch.Count)
                return (pushed, refused);     // the server stopped: the rest is for the next synchronization
            if (restamp.Count > 0)
            {
                // Once per synchronization: when the device's time is still wrong, the next synchronization tries again.
                if (fellBack)
                    return (pushed, refused);
                fellBack = true;
            }
        }
    }

    // What a later change refers to by the old dot (an addition it observed, a character it writes after or erases) it
    // now refers to by the new one.
    private static Change Rename(Change change, Dictionary<Dot, Dot> renamed)
    {
        Dot Of(Dot dot) => renamed.TryGetValue(dot, out var to) ? to : dot;
        return change with
        {
            Ops = [.. change.Ops.Select(o => o.Op switch
            {
                RemoveElement remove => o with { Op = remove with { Observed = [.. remove.Observed.Select(t => t with { Dot = Of(t.Dot) })] } },
                InsertText { After: { } after } insert => o with { Op = insert with { After = after with { Dot = Of(after.Dot) } } },
                EraseText erase => o with { Op = erase with { Spans = [.. erase.Spans.Select(s => s with { Dot = Of(s.Dot) })] } },
                _ => o,
            })],
        };
    }

    private async Task<(int Pulled, bool Snapshot)> PullAsync(IReplicationChannel channel, CancellationToken ct)
    {
        int pulled = 0;
        var snapshot = false;
        var touched = new HashSet<DocumentId>();
        string[] collections;
        long since;
        lock (_gate)
        {
            collections = [.. _collections];
            since = _cursor;
        }

        while (true)
        {
            var result = since < 0
                ? new PullResult([], since, More: false, SnapshotRequired: true)
                : await channel.PullAsync(new PullRequest(collections, since), ct);

            if (result.SnapshotRequired)
            {
                var full = await channel.SnapshotAsync(new SnapshotRequest(collections), ct);
                lock (_gate)
                {
                    touched.UnionWith(_confirmed.Documents.Select(d => d.Id));
                    _confirmed.Replace(full.Documents, collections.ToHashSet(StringComparer.Ordinal));
                    touched.UnionWith(full.Documents.Select(d => d.Id));
                    // What the snapshot includes is no longer pending: applying it again would count it twice.
                    _pending.RemoveAll(p => p.Position > 0 && p.Position <= full.Position);
                    _cursor = since = full.Position;
                }
                snapshot = true;
                await SaveAsync(ct);
                continue;
            }

            lock (_gate)
            {
                foreach (var entry in result.Entries)
                {
                    if (_confirmed.Apply(entry.Change))
                        pulled++;
                    _pending.RemoveAll(p => p.Change.Dot == entry.Change.Dot);
                    touched.UnionWith(entry.Change.Ops.Select(o => o.Document));
                }
                // An own change accepted at or before this position is in the log: pulled above, or not readable by this user
                // (another collection, a row filter). Either way it is no longer pending.
                _pending.RemoveAll(p => p.Position > 0 && p.Position <= result.Position);
                _cursor = since = result.Position;
                if (!result.More)
                {
                    // Decided before and its answer forgotten by the server (Duplicate without a position): had it been
                    // accepted, the log would have brought it back by now.
                    foreach (var lost in _pending.Where(p => p.Position == long.MaxValue).ToList())
                    {
                        _rejected.Add(new RejectedChange(lost.Change, RemoteErrorCodes.Lost,
                            "The server decided on this change before, and its answer is lost: it is not in the log."));
                        _pending.Remove(lost);
                        touched.UnionWith(lost.Change.Ops.Select(o => o.Document));
                    }
                }
            }
            await SaveAsync(ct);
            if (!result.More)
                break;
        }

        if (touched.Count > 0)
            Changed?.Invoke(touched);
        return (pulled, snapshot);
    }

    /// <summary>Forgets a refused change, once the application dealt with it.</summary>
    public async Task DismissAsync(RejectedChange rejected, CancellationToken ct = default)
    {
        lock (_gate)
            _rejected.Remove(rejected);
        await SaveAsync(ct);
    }

    private Task SaveAsync(CancellationToken ct)
    {
        byte[] state;
        lock (_gate)
        {
            state = ReplicaCodec.Write(new ReplicaCodec.StoredReplica(
                Id, Clock.Last, _nextSeq, _cursor, [.. _collections],
                [.. _confirmed.Documents.Select(d => d.ToSnapshot())], [.. _confirmed.Applied],
                [.. _pending.Select(p => (p.Change, p.Position))], [.. _rejected]));
        }
        return _storage.WriteAsync(state, ct).AsTask();
    }

    private readonly record struct Pending(Change Change, long Position);
}

/// <summary>The operations of one change, collected before it is stored.</summary>
public sealed class ChangeBuilder
{
    private readonly Replica _replica;
    private readonly List<(DocumentId, Func<Func<DocumentId, DocumentState?>, IEnumerable<FieldOp>>)> _ops = [];

    internal ChangeBuilder(Replica replica) => _replica = replica;

    // Each operation is built at commit, from what the replica sees then, with the operations before it in this change.
    private ChangeBuilder Add(DocumentId document, Func<Func<DocumentId, DocumentState?>, IEnumerable<FieldOp>> build)
    {
        _ops.Add((document, build));
        return this;
    }

    private ChangeBuilder Add(DocumentId document, Func<Func<DocumentId, DocumentState?>, FieldOp> build) => Add(document, view => [build(view)]);

    /// <summary>Creates the document (or restores it after a deletion).</summary>
    public ChangeBuilder Create(DocumentId document) => Add(document, _ => new RestoreDocument());

    public ChangeBuilder Delete(DocumentId document) => Add(document, _ => new DeleteDocument());

    public ChangeBuilder Set(DocumentId document, string field, string? value) => Add(document, _ => new SetField(field, value));

    public ChangeBuilder AddElement(DocumentId document, string field, string element) => Add(document, _ => new AddElement(field, element));

    /// <summary>Removes the element as this replica sees it now: an addition made elsewhere and not seen yet survives.</summary>
    public ChangeBuilder RemoveElement(DocumentId document, string field, string element)
        => Add(document, view => new RemoveElement(field, element, view(document)?.TagsOf(field, element) ?? []));

    public ChangeBuilder Increment(DocumentId document, string field, long delta) => Add(document, _ => new Increment(field, delta));

    /// <summary>
    /// Inserts <paramref name="text"/> in a text field at <paramref name="position"/> (in UTF-16 code units) of the text this
    /// replica sees: what others insert concurrently, at the same place or elsewhere, is kept.
    /// </summary>
    public ChangeBuilder InsertText(DocumentId document, string field, int position, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        return Add(document, view =>
        {
            var ids = view(document)?.TextIds(field) ?? [];
            if (position > ids.Count)
                throw new ArgumentOutOfRangeException(nameof(position), $"The text has {ids.Count} characters.");
            return text.Length == 0 ? [] : [new InsertText(field, position == 0 ? null : ids[position - 1], text)];
        });
    }

    /// <summary>Erases <paramref name="length"/> characters of a text field from <paramref name="position"/>, as this replica sees them.</summary>
    public ChangeBuilder EraseText(DocumentId document, string field, int position, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        return Add(document, view =>
        {
            var ids = view(document)?.TextIds(field) ?? [];
            if (position + length > ids.Count)
                throw new ArgumentOutOfRangeException(nameof(length), $"The text has {ids.Count} characters.");
            return length == 0 ? [] : [new EraseText(field, Spans(ids.Skip(position).Take(length)))];
        });
    }

    /// <summary>
    /// Gives a text field a new value with the smallest edit (the common start and end are kept), so that what others
    /// change concurrently in the rest of the text survives: what a text box bound to the field sends.
    /// </summary>
    public ChangeBuilder ReplaceText(DocumentId document, string field, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Add(document, view => TextEdit.Diff(view(document), field, text));
    }

    public int Count => _ops.Count;

    /// <summary>Stores the operations as one change, applies it to what the application sees, and queues it for the server.</summary>
    public Task<Change> CommitAsync(CancellationToken ct = default)
    {
        if (_ops.Count == 0)
            throw new InvalidOperationException("A change has at least one operation.");
        return _replica.CommitAsync(_ops, ct);
    }

    internal static List<TextSpan> Spans(IEnumerable<TextId> ids)
    {
        var spans = new List<TextSpan>();
        foreach (var id in ids)
        {
            if (spans.Count > 0 && spans[^1] is var last && last.Dot == id.Dot && last.Index == id.Index && last.Offset + last.Length == id.Offset)
                spans[^1] = last with { Length = last.Length + 1 };
            else
                spans.Add(new TextSpan(id.Dot, id.Index, id.Offset, 1));
        }
        return spans;
    }
}

/// <summary>The smallest edit from one text to another, as text operations.</summary>
internal static class TextEdit
{
    public static IEnumerable<FieldOp> Diff(DocumentState? state, string field, string text)
    {
        var current = state?.Text(field) ?? string.Empty;
        var start = 0;
        while (start < current.Length && start < text.Length && current[start] == text[start])
            start++;
        // Not into a surrogate pair: an edit starts and ends on whole characters.
        if (start > 0 && char.IsHighSurrogate(current[start - 1]))
            start--;
        var end = 0;
        while (end < current.Length - start && end < text.Length - start && current[^(end + 1)] == text[^(end + 1)])
            end++;
        if (end > 0 && char.IsLowSurrogate(current[^end]))
            end--;
        var ids = state?.TextIds(field) ?? [];
        var erased = current.Length - start - end;
        var inserted = text.Substring(start, text.Length - start - end);
        var ops = new List<FieldOp>(2);
        if (erased > 0)
            ops.Add(new EraseText(field, ChangeBuilder.Spans(ids.Skip(start).Take(erased))));
        // After the last character kept before the edit: the erased characters stay in the field as tombstones, after it.
        if (inserted.Length > 0)
            ops.Add(new InsertText(field, start == 0 ? null : ids[start - 1], inserted));
        return ops;
    }
}
