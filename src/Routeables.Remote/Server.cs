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
/// The rules of the application, on the server: which changes are admitted (Queryeables: access policies, row filters,
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

    /// <summary>How many decisions are remembered per replica, to answer a push sent again after a lost answer.</summary>
    public int RememberedAcksPerReplica { get; set; } = 1024;
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
    // One decision at a time: the order of admission is the order of the log, and a rule sees the state it decides on.
    private readonly SemaphoreSlim _admission = new(1, 1);

    public ReplicationServer(IReplicationPolicy? policy = null, ReplicationServerOptions? options = null, TimeProvider? time = null,
        IReplicationStore? store = null, ReplicaId? replica = null)
    {
        _policy = policy ?? AllowAllReplicationPolicy.Instance;
        Options = options ?? new ReplicationServerOptions();
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

    /// <summary>A channel for one connected client, authenticated as <paramref name="user"/>.</summary>
    public IReplicationChannel Connect(ClaimsPrincipal? user) => new Channel(this, user);

    /// <summary>
    /// Forgets the log up to <paramref name="position"/>: the documents keep the merged state. A client whose cursor is
    /// older gets <see cref="PullResult.SnapshotRequired"/>.
    /// </summary>
    public async Task CompactAsync(long position, CancellationToken ct = default)
    {
        await _admission.WaitAsync(ct);
        try
        {
            await Store.CompactAsync(position, ct);
        }
        finally
        {
            _admission.Release();
        }
    }

    private static string UserKey(ClaimsPrincipal? user)
        => user?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user?.Identity?.Name ?? string.Empty;

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
        if (request.Changes.Count > Options.MaxChangesPerPush)
            throw new ArgumentException($"At most {Options.MaxChangesPerPush} changes per push.", nameof(request));

        var acks = new List<ChangeAck>(request.Changes.Count);
        await _admission.WaitAsync(ct);
        try
        {
            foreach (var change in request.Changes)
                acks.Add(await AdmitAsync(user, change, ct));
        }
        finally
        {
            _admission.Release();
        }
        return new PushResult(acks);
    }

    private async Task<ChangeAck> AdmitAsync(ClaimsPrincipal? user, Change change, CancellationToken ct)
    {
        var owner = UserKey(user);
        var record = await Store.FindReplicaAsync(change.Dot.Replica, ct) ?? new ReplicaRecord(change.Dot.Replica, owner, 0, []);
        if (record.User != owner)
            return new ChangeAck(change.Dot, AckStatus.Rejected, Code: RemoteErrorCodes.ReplicaTaken, Message: "This replica belongs to another user.");
        if (change.Dot.Seq <= record.LastSeq)
        {
            var known = record.Acks.FirstOrDefault(a => a.Dot == change.Dot);
            return known is null
                ? new ChangeAck(change.Dot, AckStatus.Duplicate)
                : known with { Status = known.Status == AckStatus.Accepted ? AckStatus.Duplicate : known.Status };
        }
        if (change.Dot.Seq != record.LastSeq + 1)
            return new ChangeAck(change.Dot, AckStatus.Rejected, Code: RemoteErrorCodes.OutOfOrder,
                Message: $"The next change of this replica is {record.LastSeq + 1}.");

        // The documents of the change, as stored now: what the rules see, and what the change is merged into.
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

        LogEntry? entry = null;
        var changed = new List<DocumentState>();
        ChangeAck ack;
        if (decision.Accepted)
        {
            var head = await Store.HeadAsync(ct);
            entry = new LogEntry(head.Position + 1, change);
            foreach (var id in change.Ops.Select(o => o.Document).Distinct())
            {
                var state = await Current(id) ?? new DocumentState(id);
                for (var index = 0; index < change.Ops.Count; index++)
                    if (change.Ops[index].Document == id)
                        state.Apply(change.Ops[index].Op, new OpStamp(change.Stamp, index), change.Dot);
                changed.Add(state);
            }
            ack = new ChangeAck(change.Dot, AckStatus.Accepted, entry.Position);
        }
        else
        {
            ack = new ChangeAck(change.Dot, AckStatus.Rejected, Code: decision.Code, Message: decision.Message);
        }

        // Decided either way: the seq is used, and the next push goes on from the next one.
        var acks = record.Acks.Count < Options.RememberedAcksPerReplica ? [.. record.Acks, ack] : record.Acks.Skip(record.Acks.Count - Options.RememberedAcksPerReplica + 1).Append(ack).ToList();
        await Store.CommitAsync(new ReplicationCommit(record with { LastSeq = change.Dot.Seq, Acks = acks }, entry, changed), ct);

        if (entry is not null)
        {
            Clock.Observe(change.Stamp);
            Admitted?.Invoke(entry, [.. changed.Select(d => d.Id)]);
        }
        return ack;
    }

    // The limits every change must respect, before the rules of the application.
    private Admission Check(Change change)
    {
        if (change.Ops.Count == 0)
            return Admission.Reject(RemoteErrorCodes.Invalid, "A change has at least one operation.");
        if (change.Ops.Count > Options.MaxOpsPerChange)
            return Admission.Reject(RemoteErrorCodes.TooLarge, $"At most {Options.MaxOpsPerChange} operations per change.");
        if (change.Stamp.Replica != change.Dot.Replica)
            return Admission.Reject(RemoteErrorCodes.Invalid, "A change is stamped by its own replica.");
        if (change.Stamp.Wall > LatestAllowedWall())
            return Admission.Reject(RemoteErrorCodes.ClockSkew, "The change is stamped in the future: the clock of the device is wrong.");
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
        // Between two decisions: the documents hold exactly the log up to the position given with them.
        await _admission.WaitAsync(ct);
        try
        {
            var head = await Store.HeadAsync(ct);
            var documents = await Store.DocumentsAsync(collections, ct);
            return new SnapshotResult([.. documents.Where(d => _policy.CanRead(user, d.Id, d)).Select(d => d.ToSnapshot())], head.Position);
        }
        finally
        {
            _admission.Release();
        }
    }

    private sealed class Channel(ReplicationServer server, ClaimsPrincipal? user) : IReplicationChannel
    {
        public Task<RemoteWelcome> HelloAsync(RemoteHello hello, CancellationToken ct = default) => server.HelloAsync(user, hello, ct);

        public Task<PushResult> PushAsync(PushRequest request, CancellationToken ct = default) => server.PushAsync(user, request, ct);

        public Task<PullResult> PullAsync(PullRequest request, CancellationToken ct = default) => server.PullAsync(user, request, ct);

        public Task<SnapshotResult> SnapshotAsync(SnapshotRequest request, CancellationToken ct = default) => server.SnapshotAsync(user, request, ct);
    }
}
