using System.Text.Json;
using System.Text.Json.Serialization;

namespace Routeables.Remote;

/// <summary>The version of the messages. A client says it first (<see cref="RemoteHello"/>); a server of another version refuses it.</summary>
public static class RemoteProtocol
{
    public const int Version = 1;
}

/// <summary>
/// What a client says first, and again after each reconnection. It carries everything the server needs to resume: no
/// session is kept on the server between two connections.
/// </summary>
/// <param name="Protocol"><see cref="RemoteProtocol.Version"/>.</param>
/// <param name="Replica">The replica of the client.</param>
/// <param name="Clock">The clock of the client: the server moves its own past it.</param>
public sealed record RemoteHello(int Protocol, ReplicaId Replica, Hlc Clock);

/// <summary>The answer to <see cref="RemoteHello"/>.</summary>
/// <param name="Success">Whether the client may go on.</param>
/// <param name="Protocol">The version of the server.</param>
/// <param name="Clock">The clock of the server: the client moves its own past it, so that its next changes come after what it will pull.</param>
/// <param name="LastSeq">The last change of this replica the server has decided on (accepted or refused): a client pushes from the next one.</param>
/// <param name="MaxChangesPerPush">How many changes one <see cref="PushRequest"/> may carry.</param>
/// <param name="Error">Why the client may not go on.</param>
public sealed record RemoteWelcome(bool Success, int Protocol, Hlc Clock, long LastSeq, int MaxChangesPerPush, string? Error = null);

/// <summary>Changes of the client, in the order it made them, starting right after the last one the server decided on.</summary>
public sealed record PushRequest(IReadOnlyList<Change> Changes);

/// <summary>What the server decided for each change pushed.</summary>
public sealed record PushResult(IReadOnlyList<ChangeAck> Acks);

/// <summary>The decision on one change.</summary>
public enum AckStatus
{
    /// <summary>Admitted to the log at <see cref="ChangeAck.Position"/>.</summary>
    Accepted = 1,

    /// <summary>Refused: it is not in the log and never will be (<see cref="ChangeAck.Code"/> says why).</summary>
    Rejected = 2,

    /// <summary>Already decided before (its answer was lost): nothing was done again.</summary>
    Duplicate = 3,
}

/// <summary>The decision on one change.</summary>
/// <param name="Dot">The change.</param>
/// <param name="Status">Accepted, rejected or already decided.</param>
/// <param name="Position">Where it is in the log, when accepted (and when a duplicate of an accepted change still remembered).</param>
/// <param name="Code">Why it was refused (<see cref="RemoteErrorCodes"/>).</param>
/// <param name="Message">Why, in words.</param>
public sealed record ChangeAck(Dot Dot, AckStatus Status, long Position = 0, string? Code = null, string? Message = null);

/// <summary>The changes after <see cref="Since"/> that touch <see cref="Collections"/> and that the user may read.</summary>
/// <param name="Collections">The collections the client replicates.</param>
/// <param name="Since">The last position the client received (its cursor); 0 for everything.</param>
/// <param name="Max">At most this many changes (the server may send fewer).</param>
public sealed record PullRequest(IReadOnlyList<string> Collections, long Since, int Max = 500);

/// <summary>A part of the log.</summary>
/// <param name="Entries">The changes, by position; each reduced to the operations the user may read.</param>
/// <param name="Position">The new cursor: every position up to it was considered, sent or not.</param>
/// <param name="More">Whether there is more after it: pull again.</param>
/// <param name="SnapshotRequired">The log does not go back to <see cref="PullRequest.Since"/> any more: ask a snapshot.</param>
public sealed record PullResult(IReadOnlyList<LogEntry> Entries, long Position, bool More, bool SnapshotRequired = false);

/// <summary>A change admitted to the log, and its position there.</summary>
public sealed record LogEntry(long Position, Change Change);

/// <summary>Every document of <see cref="Collections"/> the user may read, as merged up to now.</summary>
public sealed record SnapshotRequest(IReadOnlyList<string> Collections);

/// <summary>The documents, and the position they include everything up to: the cursor to pull from next.</summary>
public sealed record SnapshotResult(IReadOnlyList<DocumentSnapshot> Documents, long Position);

/// <summary>
/// Told by the server to the connected clients of a collection when the log grows: a hint to pull, never the data itself.
/// A notification that is lost costs nothing but delay: the next pull gets the changes.
/// </summary>
public sealed record LogAdvanced(IReadOnlyList<string> Collections, long Position);

/// <summary>Why a change was refused.</summary>
public static class RemoteErrorCodes
{
    /// <summary>The access rules of the server refuse it (policy, row filter, role).</summary>
    public const string Forbidden = "forbidden";

    /// <summary>The rules of the application refuse it (validation, a constraint).</summary>
    public const string Invalid = "invalid";

    /// <summary>Too large: too many operations, a value too long.</summary>
    public const string TooLarge = "too-large";

    /// <summary>Stamped too far in the future: the client's clock is wrong. The client sends the same intent again, stamped anew.</summary>
    public const string ClockSkew = "clock-skew";

    /// <summary>The replica belongs to another user.</summary>
    public const string ReplicaTaken = "replica-taken";

    /// <summary>A change is missing before this one: push from <see cref="RemoteWelcome.LastSeq"/> + 1.</summary>
    public const string OutOfOrder = "out-of-order";

    /// <summary>
    /// Client side only: the server decided on the change long ago and no longer remembers how, and the log does not hold
    /// it. It was refused.
    /// </summary>
    public const string Lost = "lost";
}

/// <summary>The messages as JSON, without reflection (Native AOT). MessagePack transports map the same records.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(RemoteHello))]
[JsonSerializable(typeof(RemoteWelcome))]
[JsonSerializable(typeof(PushRequest))]
[JsonSerializable(typeof(PushResult))]
[JsonSerializable(typeof(PullRequest))]
[JsonSerializable(typeof(PullResult))]
[JsonSerializable(typeof(SnapshotRequest))]
[JsonSerializable(typeof(SnapshotResult))]
[JsonSerializable(typeof(LogAdvanced))]
public sealed partial class RemoteJsonContext : JsonSerializerContext;
