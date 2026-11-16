using System.Security.Claims;

namespace Routeables.Remote;

/// <summary>
/// What a client calls on the server. Transports (MagicOnion, SignalR, HTTP) carry these four calls and nothing else; the
/// in-process implementation is <see cref="ReplicationServer.Connect"/>.
/// </summary>
public interface IReplicationChannel
{
    Task<RemoteWelcome> HelloAsync(RemoteHello hello, CancellationToken ct = default);

    Task<PushResult> PushAsync(PushRequest request, CancellationToken ct = default);

    Task<PullResult> PullAsync(PullRequest request, CancellationToken ct = default);

    Task<SnapshotResult> SnapshotAsync(SnapshotRequest request, CancellationToken ct = default);
}

/// <summary>What the server decides about a change: admit it, or refuse it with a reason.</summary>
public readonly record struct Admission(bool Accepted, string? Code = null, string? Message = null)
{
    public static Admission Accept { get; } = new(true);

    public static Admission Reject(string code, string message) => new(false, code, message);
}

/// <summary>
/// The rules of the application, on the server: which changes are admitted (access policies, row filters,
/// validation; Routeables.SignalR: settable properties, roles), and which documents a user may read.
/// </summary>
public interface IReplicationPolicy
{
    /// <summary>
    /// Decides on a whole change. <paramref name="current"/> gives the merged state of a document before the change (null
    /// when it does not exist yet): a rule may depend on who owns the row now, not only on what the change says.
    /// </summary>
    ValueTask<Admission> AdmitAsync(ClaimsPrincipal? user, Change change, Func<DocumentId, ValueTask<DocumentState?>> current, CancellationToken ct);

    /// <summary>Whether the user may read a document: decides what a pull or a snapshot sends to this user.</summary>
    bool CanRead(ClaimsPrincipal? user, DocumentId document, DocumentState? state);

    /// <summary>
    /// Whether the user is told when a collection moves on (<c>LogAdvanced</c>: its name and the position, never a document). Every user
    /// by default; refuse it for a collection whose activity alone tells too much (the user still pulls what he may read).
    /// </summary>
    bool CanFollow(ClaimsPrincipal? user, string collection) => true;
}

/// <summary>Admits everything, lets everyone read everything: tests and closed networks only.</summary>
public sealed class AllowAllReplicationPolicy : IReplicationPolicy
{
    public static AllowAllReplicationPolicy Instance { get; } = new();

    public ValueTask<Admission> AdmitAsync(ClaimsPrincipal? user, Change change, Func<DocumentId, ValueTask<DocumentState?>> current, CancellationToken ct)
        => ValueTask.FromResult(Admission.Accept);

    public bool CanRead(ClaimsPrincipal? user, DocumentId document, DocumentState? state) => true;
}

/// <summary>Limits of the server.</summary>
public sealed class ReplicationServerOptions
{
    /// <summary>How far in the future a change may be stamped. Further: <see cref="RemoteErrorCodes.ClockSkew"/>.</summary>
    public TimeSpan MaxClockSkew { get; set; } = TimeSpan.FromMinutes(5);

    public int MaxChangesPerPush { get; set; } = 256;

    public int MaxOpsPerChange { get; set; } = 1000;

    public int MaxValueLength { get; set; } = 64 * 1024;

    public int MaxPullEntries { get; set; } = 1000;

    /// <summary>How many collections one pull or one snapshot may name.</summary>
    public int MaxCollectionsPerPull { get; set; } = 64;

    /// <summary>
    /// How many collections one connection may follow (<see cref="LogAdvanced"/>), all its pulls together: what the server keeps for it
    /// stays bounded. 256 by default.
    /// </summary>
    public int MaxCollectionsPerConnection { get; set; } = 256;

    /// <summary>How many characters one text field may hold, the erased ones included (they stay for the anchors). 1 000 000 by default.</summary>
    public int MaxTextLength { get; set; } = 1_000_000;

    /// <summary>
    /// How many snapshots are made at once. A snapshot does not stop the writes (it reads the documents between two of them), but each
    /// one reads whole collections. 4 by default.
    /// </summary>
    public int MaxConcurrentSnapshots { get; set; } = 4;

    /// <summary>How many decisions are remembered per replica, to answer a push sent again after a lost answer.</summary>
    public int RememberedAcksPerReplica { get; set; } = 1024;

    /// <summary>
    /// Whether the changes of different replicas are decided at the same time. Each change is decided (its replica and its documents read, the
    /// rules of the policy) alongside the others, then written one at a time, in log order; a decision whose documents changed meanwhile (those
    /// the rules read included) is made again, and after a few attempts made while no other decision is written. A replica still decides one
    /// change at a time. Set it to <see langword="false"/> for a policy that cannot be called from several threads at once: the decisions are
    /// then made one at a time, as in version 1. <see langword="true"/> by default.
    /// </summary>
    public bool ConcurrentAdmission { get; set; } = true;
}

/// <summary>
/// The server side of the protocol: a privileged replica that decides which changes enter the log, numbers them, merges
/// them, and serves them to the clients from their cursor. What it keeps is in an <see cref="IReplicationStore"/>: in
/// memory by default, in a database for a durable server.
/// </summary>
public sealed class ReplicationServer
{
    private readonly IReplicationPolicy _policy;
    private readonly TimeProvider _time;
    // One write at a time: the order of the writes is the order of the log. A decision is checked under it before it is written: the documents
    // it read did not change since (a rule saw the state it decides on), or it is made again.
    private readonly SemaphoreSlim _commit = new(1, 1);
    // One decision per replica at a time: its seq moves on in order, and its record does not change under a decision.
    private readonly KeyedLocks<ReplicaId> _replicas = new();
    // The writes counted, and, per stripe of documents, the count of the last write that changed one of them (under _commit).
    private const int Stripes = 4096;
    private const int MaxAdmissionAttempts = 4;
    private readonly long[] _writtenAt = new long[Stripes];
    private long _writes;
    private readonly SemaphoreSlim _snapshots;

    /// <param name="policy">
    /// Who may write and read what: always given, since nothing is shared without a rule (<see cref="AllowAllReplicationPolicy"/> lets anyone
    /// identified write and read everything, on purpose).
    /// </param>
    /// <param name="options">The limits.</param>
    /// <param name="time">The clock (tests).</param>
    /// <param name="store">Where the log and the documents are kept: memory by default.</param>
    /// <param name="replica">The replica of the server's clock.</param>
    public ReplicationServer(IReplicationPolicy policy, ReplicationServerOptions? options = null, TimeProvider? time = null,
        IReplicationStore? store = null, ReplicaId? replica = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _policy = policy;
        Options = options ?? new ReplicationServerOptions();
        _snapshots = new SemaphoreSlim(Math.Max(1, Options.MaxConcurrentSnapshots));
        _time = time ?? TimeProvider.System;
        Store = store ?? new MemoryReplicationStore();
        Clock = new HybridClock(replica ?? ReplicaId.New(), _time);
    }

    public ReplicationServerOptions Options { get; }

    public HybridClock Clock { get; }

    public IReplicationStore Store { get; }

    /// <summary>The merged state of a document, as the server holds it.</summary>
    public ValueTask<DocumentState?> FindAsync(DocumentId id, CancellationToken ct = default) => Store.FindAsync(id, ct);

    /// <summary>
    /// Raised after each change admitted and stored, in log order: an adapter tells the connected clients
    /// (<see cref="LogAdvanced"/>) or updates a read model.
    /// </summary>
    public event Action<LogEntry, IReadOnlyCollection<DocumentId>>? Admitted;

    /// <summary>Whether <paramref name="user"/> is told when <paramref name="collection"/> moves on (<see cref="IReplicationPolicy.CanFollow"/>).</summary>
    public bool CanFollow(ClaimsPrincipal? user, string collection) => _policy.CanFollow(user, collection);

    /// <summary>A channel for one connected client, authenticated as <paramref name="user"/>.</summary>
    public IReplicationChannel Connect(ClaimsPrincipal? user) => new Channel(this, user);

    /// <summary>
    /// Forgets the log up to <paramref name="position"/>: the documents keep the merged state. A client whose cursor is
    /// older gets <see cref="PullResult.SnapshotRequired"/>.
    /// </summary>
    public async Task CompactAsync(long position, CancellationToken ct = default)
    {
        await _commit.WaitAsync(ct);
        try
        {
            await Store.CompactAsync(position, ct);
        }
        finally
        {
            _commit.Release();
        }
    }

    /// <summary>
    /// Who owns a replica: the scheme, the issuer and the identifier of an authenticated user (<c>NameIdentifier</c>, else <c>sub</c>), as for
    /// the sessions of Routeables.SignalR; <see langword="null"/> for a user that cannot be told apart from another one (anonymous, or
    /// without an identifier), who then writes nothing.
    /// </summary>
    internal static string? UserKey(ClaimsPrincipal? user)
    {
        var identity = user?.Identity;
        if (identity is not { IsAuthenticated: true })
            return null;
        var claim = user!.FindFirst(ClaimTypes.NameIdentifier) ?? user.FindFirst("sub");
        if (claim is null || string.IsNullOrWhiteSpace(claim.Value))
            return null;
        var scheme = identity.AuthenticationType ?? string.Empty;
        return $"{scheme.Length}:{scheme}|{claim.Issuer.Length}:{claim.Issuer}|{claim.Value}";
    }

    private long LatestAllowedWall() => _time.GetUtcNow().Add(Options.MaxClockSkew).ToUnixTimeMilliseconds();

    private async Task<RemoteWelcome> HelloAsync(ClaimsPrincipal? user, RemoteHello hello, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(hello);
        if (hello.Protocol != RemoteProtocol.Version)
            return new RemoteWelcome(false, RemoteProtocol.Version, Clock.Last, 0, Options.MaxChangesPerPush,
                $"This server speaks version {RemoteProtocol.Version} of the protocol, the client {hello.Protocol}: update the client.");
        var record = await Store.FindReplicaAsync(hello.Replica, ct);
        if (record is not null && record.User != UserKey(user))
            return new RemoteWelcome(false, RemoteProtocol.Version, Clock.Last, 0, Options.MaxChangesPerPush,
                "This replica belongs to another user: a new user starts with a new replica.");
        // A clock too far ahead is not followed: it would carry every replica into its future. Its changes are refused
        // (ClockSkew) and stamped again by the client after it has fallen back to the clock of the server.
        if (hello.Clock.Wall <= LatestAllowedWall())
            Clock.Observe(hello.Clock);
        return new RemoteWelcome(true, RemoteProtocol.Version, Clock.Last, record?.LastSeq ?? 0, Options.MaxChangesPerPush);
    }

    private async Task<PushResult> PushAsync(ClaimsPrincipal? user, PushRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Changes is null || request.Changes.Any(c => c is null))
            throw new ArgumentException("A push holds changes.", nameof(request));
        if (request.Changes.Count > Options.MaxChangesPerPush)
            throw new ArgumentException($"At most {Options.MaxChangesPerPush} changes per push.", nameof(request));

        // The changes of a push are decided in order; the pushes of other replicas are decided meanwhile (ConcurrentAdmission).
        var acks = new List<ChangeAck>(request.Changes.Count);
        foreach (var change in request.Changes)
            acks.Add(await AdmitAsync(user, change, ct));
        return new PushResult(acks);
    }

    private async Task<ChangeAck> AdmitAsync(ClaimsPrincipal? user, Change change, CancellationToken ct)
    {
        // Anyone may read the replica of a change in the log: only a user told apart from the others may own one.
        if (UserKey(user) is not { } owner)
            return new ChangeAck(change.Dot, AckStatus.Rejected, Code: RemoteErrorCodes.Unidentified,
                Message: "An anonymous user, or one without an identifier, cannot write: sign in.");

        using var replica = await _replicas.LockAsync(change.Dot.Replica, ct);
        for (var attempt = 1; ; attempt++)
        {
            // The last attempt, or every one when decisions are not concurrent, is made while nothing else is written: it cannot be outdated.
            var exclusive = !Options.ConcurrentAdmission || attempt >= MaxAdmissionAttempts;
            var held = false;
            try
            {
                if (exclusive)
                {
                    await _commit.WaitAsync(ct);
                    held = true;
                }
                var seen = Volatile.Read(ref _writes);
                var (final, decided) = await DecideAsync(user, owner, change, ct);
                if (final is not null)
                    return final;

                if (!held)
                {
                    await _commit.WaitAsync(ct);
                    held = true;
                    if (Outdated(decided!.Read, seen))
                        continue;   // a document it read was written meanwhile: decided again on what is stored now
                }
                return await WriteAsync(change, decided!, ct);
            }
            finally
            {
                if (held)
                    _commit.Release();
            }
        }
    }

    // What a decision found, before it is written: the record of the replica, the answer, the documents it changes, and those it read.
    private sealed record Decided(ReplicaRecord Record, Admission Admission, List<DocumentState> Changed, IReadOnlyCollection<DocumentId> Read);

    private static int Stripe(DocumentId id) => (id.GetHashCode() & int.MaxValue) % Stripes;

    // Under _commit: whether a document read after the write numbered seen was written since.
    private bool Outdated(IReadOnlyCollection<DocumentId> read, long seen)
    {
        foreach (var id in read)
            if (_writtenAt[Stripe(id)] > seen)
                return true;
        return false;
    }

    // Decides on a change, under the lock of its replica: either a final answer that writes nothing (a change sent again, out of order, or of
    // a replica of another user), or a decision to write.
    private async Task<(ChangeAck? Final, Decided? Decided)> DecideAsync(ClaimsPrincipal? user, string owner, Change change, CancellationToken ct)
    {
        var record = await Store.FindReplicaAsync(change.Dot.Replica, ct) ?? new ReplicaRecord(change.Dot.Replica, owner, 0, []);
        if (record.User != owner)
            return (new ChangeAck(change.Dot, AckStatus.Rejected, Code: RemoteErrorCodes.ReplicaTaken, Message: "This replica belongs to another user."), null);
        if (change.Dot.Seq <= record.LastSeq)
        {
            var known = record.Acks.FirstOrDefault(a => a.Dot == change.Dot);
            return (known is null
                ? new ChangeAck(change.Dot, AckStatus.Duplicate)
                : known with { Status = known.Status == AckStatus.Accepted ? AckStatus.Duplicate : known.Status }, null);
        }
        if (change.Dot.Seq != record.LastSeq + 1)
            return (new ChangeAck(change.Dot, AckStatus.Rejected, Code: RemoteErrorCodes.OutOfOrder,
                Message: $"The next change of this replica is {record.LastSeq + 1}."), null);

        // The documents of the change, as stored now: what the rules see, and what the change is merged into. Every document read is kept,
        // to check before writing that none changed since.
        var documents = new Dictionary<DocumentId, DocumentState?>();
        async ValueTask<DocumentState?> Current(DocumentId id)
        {
            if (!documents.TryGetValue(id, out var state))
                documents[id] = state = await Store.FindAsync(id, ct);
            return state?.Clone();
        }

        var decision = Check(change);
        if (decision.Accepted)
            decision = await _policy.AdmitAsync(user, change, Current, ct);

        var changed = new List<DocumentState>();
        if (decision.Accepted)
        {
            foreach (var id in change.Ops.Select(o => o.Document).Distinct())
            {
                var state = await Current(id) ?? new DocumentState(id);
                for (var index = 0; index < change.Ops.Count; index++)
                {
                    if (change.Ops[index].Document != id)
                        continue;
                    // The sum of a counter stays where every implementation adds it exactly.
                    if (change.Ops[index].Op is Increment increment && Math.Abs(state.Counter(increment.Field) + increment.Delta) > RemoteProtocol.MaxCounter)
                        decision = Admission.Reject(RemoteErrorCodes.TooLarge, $"The counter '{increment.Field}' would go beyond ±{RemoteProtocol.MaxCounter}.");
                    if (change.Ops[index].Op is InsertText insert && (long)state.TextSize(insert.Field) + insert.Text.Length > Options.MaxTextLength)
                        decision = Admission.Reject(RemoteErrorCodes.TooLarge, $"The text '{insert.Field}' would hold more than {Options.MaxTextLength} characters.");
                    state.Apply(change.Ops[index].Op, new OpStamp(change.Stamp, index), change.Dot);
                }
                changed.Add(state);
            }
            if (!decision.Accepted)
                changed.Clear();
        }
        return (null, new Decided(record, decision, changed, documents.Keys));
    }

    // Under _commit: numbers the change when it was accepted, and writes the decision either way.
    private async Task<ChangeAck> WriteAsync(Change change, Decided decided, CancellationToken ct)
    {
        var (record, decision, changed, _) = decided;
        LogEntry? entry = null;
        ChangeAck ack;
        if (decision.Accepted)
        {
            var head = await Store.HeadAsync(ct);
            entry = new LogEntry(head.Position + 1, change);
            ack = new ChangeAck(change.Dot, AckStatus.Accepted, entry.Position);
        }
        else
        {
            ack = new ChangeAck(change.Dot, AckStatus.Rejected, Code: decision.Code, Message: decision.Message);
        }

        // Decided either way: the seq is used, and the next push goes on from the next one.
        var acks = record.Acks.Count < Options.RememberedAcksPerReplica ? [.. record.Acks, ack] : record.Acks.Skip(record.Acks.Count - Options.RememberedAcksPerReplica + 1).Append(ack).ToList();
        await Store.CommitAsync(new ReplicationCommit(record with { LastSeq = change.Dot.Seq, Acks = acks }, entry, changed), ct);

        // The documents written are marked before the count moves on: a decision that read them after its count was taken is outdated.
        var writes = _writes + 1;
        foreach (var document in changed)
            _writtenAt[Stripe(document.Id)] = writes;
        Volatile.Write(ref _writes, writes);

        if (entry is not null)
        {
            Clock.Observe(change.Stamp);
            Admitted?.Invoke(entry, [.. changed.Select(d => d.Id)]);
        }
        return ack;
    }

    // What a change must hold to be read at all: what is deserialized may lack anything.
    private static string? Malformed(Change change)
    {
        if (change.Ops is null)
            return "A change has operations.";
        foreach (var op in change.Ops)
        {
            if (op is null || op.Document.Collection is null || op.Document.Key is null || op.Op is null)
                return "An operation names its document and what it does.";
            var complete = op.Op switch
            {
                SetField set => set.Field is not null,
                AddElement add => add.Field is not null && add.Element is not null,
                RemoveElement remove => remove.Field is not null && remove.Element is not null && remove.Observed is not null,
                Increment increment => increment.Field is not null,
                InsertText insert => insert.Field is not null && insert.Text is not null,
                EraseText erase => erase.Field is not null && erase.Spans is not null,
                _ => true,
            };
            if (!complete)
                return $"An operation '{op.Op.GetType().Name}' lacks a field, an element, a text or a list.";
        }
        return null;
    }

    // The limits every change must respect, before the rules of the application.
    private Admission Check(Change change)
    {
        if (Malformed(change) is { } malformed)
            return Admission.Reject(RemoteErrorCodes.Invalid, malformed);
        if (change.Ops.Count == 0)
            return Admission.Reject(RemoteErrorCodes.Invalid, "A change has at least one operation.");
        if (change.Ops.Count > Options.MaxOpsPerChange)
            return Admission.Reject(RemoteErrorCodes.TooLarge, $"At most {Options.MaxOpsPerChange} operations per change.");
        if (change.Stamp.Replica != change.Dot.Replica)
            return Admission.Reject(RemoteErrorCodes.Invalid, "A change is stamped by its own replica.");
        if (change.Stamp.Wall > LatestAllowedWall())
            return Admission.Reject(RemoteErrorCodes.ClockSkew, "The change is stamped in the future: the clock of the device is wrong.");
        if (change.Stamp.Counter < 0)
            return Admission.Reject(RemoteErrorCodes.Invalid, "The counter of a stamp starts at 0.");
        foreach (var op in change.Ops)
        {
            var length = op.Op switch
            {
                SetField set => set.Value?.Length ?? 0,
                AddElement add => add.Element.Length,
                RemoveElement remove => remove.Element.Length + remove.Observed.Count,
                InsertText insert => insert.Text.Length,
                EraseText erase => erase.Spans.Count,
                _ => 0,
            };
            if (op.Op is Increment { Delta: < -RemoteProtocol.MaxCounter or > RemoteProtocol.MaxCounter })
                return Admission.Reject(RemoteErrorCodes.TooLarge, $"An increment is at most ±{RemoteProtocol.MaxCounter}.");
            if (length > Options.MaxValueLength || op.Document.Key.Length > 256 || op.Document.Collection.Length > 256)
                return Admission.Reject(RemoteErrorCodes.TooLarge, "A value, a key or a collection name is too long.");
            if (op.Op is InsertText { After: { } after } && after.Dot.Seq <= 0 || op.Op is EraseText e && e.Spans.Any(s => s.Length <= 0))
                return Admission.Reject(RemoteErrorCodes.Invalid, "A text operation refers to no character.");
        }
        return Admission.Accept;
    }

    private HashSet<string> Collections(IReadOnlyList<string> collections)
    {
        ArgumentNullException.ThrowIfNull(collections);
        if (collections.Any(c => c is null))
            throw new ArgumentException("A collection has a name.", nameof(collections));
        if (collections.Count > Options.MaxCollectionsPerPull)
            throw new ArgumentException($"At most {Options.MaxCollectionsPerPull} collections per pull.", nameof(collections));
        return collections.ToHashSet(StringComparer.Ordinal);
    }

    private async Task<PullResult> PullAsync(ClaimsPrincipal? user, PullRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var collections = Collections(request.Collections);
        var max = Math.Clamp(request.Max, 1, Options.MaxPullEntries);
        var head = await Store.HeadAsync(ct);
        if (request.Since < head.Horizon)
            return new PullResult([], request.Since, More: false, SnapshotRequired: true);

        var read = await Store.ReadAsync(request.Since, head.Position, collections, max, ct);
        // The log may have been compacted between the two reads: the entries after the cursor that were removed are not in what was
        // read, and the client would move past them. It starts again from a snapshot instead.
        if (request.Since < (await Store.HeadAsync(ct)).Horizon)
            return new PullResult([], request.Since, More: false, SnapshotRequired: true);
        var visibility = new Dictionary<DocumentId, bool>();
        var entries = new List<LogEntry>(read.Count);
        foreach (var entry in read)
        {
            // Only the operations the user may read; the position moves on even when nothing is sent.
            var visible = new List<DocumentOp>(entry.Change.Ops.Count);
            foreach (var op in entry.Change.Ops)
            {
                if (!collections.Contains(op.Document.Collection))
                    continue;
                if (!visibility.TryGetValue(op.Document, out var canRead))
                    visibility[op.Document] = canRead = _policy.CanRead(user, op.Document, await Store.FindAsync(op.Document, ct));
                if (canRead)
                    visible.Add(op);
            }
            if (visible.Count > 0)
                entries.Add(visible.Count == entry.Change.Ops.Count ? entry : entry with { Change = entry.Change with { Ops = visible } });
        }
        var position = read.Count == max ? read[^1].Position : head.Position;
        return new PullResult(entries, position, More: position < head.Position);
    }

    private async Task<SnapshotResult> SnapshotAsync(ClaimsPrincipal? user, SnapshotRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var collections = Collections(request.Collections);
        await _snapshots.WaitAsync(ct);
        try
        {
            // The documents hold exactly the log up to the position given with them. They are read between two decisions without
            // stopping the writes: when no change was admitted while they were read (the position did not move; a decision writes its
            // entry and its documents at once), they are those of that position. A log too busy for that is read between two decisions.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var before = await Store.HeadAsync(ct);
                var documents = await Store.DocumentsAsync(collections, ct);
                if ((await Store.HeadAsync(ct)).Position == before.Position)
                    return Snapshot(user, documents, before.Position);
            }
            await _commit.WaitAsync(ct);
            try
            {
                var head = await Store.HeadAsync(ct);
                return Snapshot(user, await Store.DocumentsAsync(collections, ct), head.Position);
            }
            finally
            {
                _commit.Release();
            }
        }
        finally
        {
            _snapshots.Release();
        }
    }

    private SnapshotResult Snapshot(ClaimsPrincipal? user, IReadOnlyList<DocumentState> documents, long position)
        => new([.. documents.Where(d => _policy.CanRead(user, d.Id, d)).Select(d => d.ToSnapshot())], position);

    private sealed class Channel(ReplicationServer server, ClaimsPrincipal? user) : IReplicationChannel
    {
        public Task<RemoteWelcome> HelloAsync(RemoteHello hello, CancellationToken ct = default) => server.HelloAsync(user, hello, ct);

        public Task<PushResult> PushAsync(PushRequest request, CancellationToken ct = default) => server.PushAsync(user, request, ct);

        public Task<PullResult> PullAsync(PullRequest request, CancellationToken ct = default) => server.PullAsync(user, request, ct);

        public Task<SnapshotResult> SnapshotAsync(SnapshotRequest request, CancellationToken ct = default) => server.SnapshotAsync(user, request, ct);
    }
}

/// <summary>A lock per key, created when first taken and dropped when nobody holds or waits for it.</summary>
internal sealed class KeyedLocks<TKey> where TKey : notnull
{
    private readonly Dictionary<TKey, Entry> _entries = [];

    private sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public int Users;
    }

    public async Task<IDisposable> LockAsync(TKey key, CancellationToken ct)
    {
        Entry entry;
        lock (_entries)
        {
            if (!_entries.TryGetValue(key, out entry!))
                _entries[key] = entry = new Entry();
            entry.Users++;
        }
        try
        {
            await entry.Gate.WaitAsync(ct);
        }
        catch
        {
            Leave(key, entry);
            throw;
        }
        return new Releaser(this, key, entry);
    }

    private void Leave(TKey key, Entry entry)
    {
        lock (_entries)
            if (--entry.Users == 0)
                _entries.Remove(key);
    }

    private sealed class Releaser : IDisposable
    {
        private KeyedLocks<TKey>? _owner;
        private readonly TKey _key;
        private readonly Entry _entry;

        public Releaser(KeyedLocks<TKey> owner, TKey key, Entry entry) => (_owner, _key, _entry) = (owner, key, entry);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is not { } owner)
                return;
            _entry.Gate.Release();
            owner.Leave(_key, _entry);
        }
    }
}
