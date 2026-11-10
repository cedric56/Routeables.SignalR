using System.Text.Json;

namespace Routeables.SignalR;

/// <summary>
/// The version of the Essentials messages: the server calls the device APIs of the client (geolocation, preferences, connectivity…) and the
/// client tells their events. A client says which version it speaks in <see cref="EssentialsReady"/>.
/// </summary>
public static class EssentialsProtocol
{
    /// <summary>1: <see cref="EssentialsReady"/>, <see cref="EssentialsCall"/>, <see cref="EssentialsResult"/>, <see cref="EssentialsEvent"/>, <see cref="EssentialsSubscription"/>.</summary>
    public const int Version = 1;

    /// <summary>The longest name of a service, a method or an event.</summary>
    public const int MaxNameLength = 64;

    /// <summary>How many services a client may announce.</summary>
    public const int MaxCapabilities = 64;

    internal static bool IsName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength)
            return false;
        foreach (var c in name)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
                return false;
        return true;
    }
}

/// <summary>
/// What the client says once its session is open (after <c>Hello</c>, and again after each reconnection): the Essentials services it implements
/// and their state, so that the synchronous members (<c>Preferences.Get</c>, <c>Connectivity.NetworkAccess</c>…) are answered on the server
/// without a round trip.
/// </summary>
/// <param name="Version">The version of the messages the client speaks (<see cref="EssentialsProtocol.Version"/>).</param>
/// <param name="Capabilities">The services the client implements: <c>preferences</c>, <c>connectivity</c>…</param>
/// <param name="State">The state of the services that have one, by service.</param>
public sealed record EssentialsReady(int Version, IReadOnlyList<string> Capabilities, IReadOnlyDictionary<string, JsonElement>? State = null);

/// <summary>A call of a device API of the client. The client answers with <see cref="EssentialsResult"/>, carrying the same <see cref="Id"/>.</summary>
/// <param name="Id">Identifies the call in the session.</param>
/// <param name="Service">The service (<c>preferences</c>).</param>
/// <param name="Method">The method (<c>set</c>); only the methods of the protocol of the service are called.</param>
/// <param name="Args">The arguments, in order.</param>
public sealed record EssentialsCall(string Id, string Service, string Method, IReadOnlyList<object?> Args);

/// <summary>The answer of the client to an <see cref="EssentialsCall"/>.</summary>
/// <param name="Id">The <see cref="EssentialsCall.Id"/> of the call.</param>
/// <param name="Success">Whether the call succeeded.</param>
/// <param name="Result">What it returned.</param>
/// <param name="Error">Why it failed.</param>
/// <param name="Code">What kind of failure (<see cref="EssentialsErrorCodes"/>).</param>
public sealed record EssentialsResult(string Id, bool Success, JsonElement? Result = null, string? Error = null, string? Code = null);

/// <summary>An event of a device API of the client, for a subscription the server asked (<see cref="EssentialsSubscription"/>).</summary>
public sealed record EssentialsEvent(string Service, string Event, JsonElement? Payload = null);

/// <summary>
/// Starts (<see cref="Active"/>) or stops an event of the client. The server sends the active ones again after each <see cref="EssentialsReady"/>:
/// a client that reconnected does not need to remember them.
/// </summary>
public sealed record EssentialsSubscription(string Service, string Event, bool Active);

/// <summary>The kinds of failure of an Essentials call, in <see cref="EssentialsResult.Code"/> and <see cref="RouteableEssentialsException.Code"/>.</summary>
public static class EssentialsErrorCodes
{
    /// <summary>The client does not implement the service or the method (or the browser lacks the API).</summary>
    public const string NotSupported = "not-supported";

    /// <summary>The user or the platform refused the permission.</summary>
    public const string PermissionDenied = "permission-denied";

    /// <summary>The feature exists but is turned off (location services off).</summary>
    public const string NotEnabled = "not-enabled";

    /// <summary>The client did not answer in time.</summary>
    public const string Timeout = "timeout";

    /// <summary>An argument the client could not use.</summary>
    public const string InvalidArgument = "invalid-argument";

    /// <summary>Anything else.</summary>
    public const string Failed = "failed";

    /// <summary>The call was cancelled (by the caller, or because the session ended).</summary>
    public const string Cancelled = "cancelled";

    /// <summary>The client has not said what it supports yet (<see cref="EssentialsReady"/>).</summary>
    public const string NotReady = "not-ready";

    /// <summary>The connection that received the call dropped: it may or may not have run on the client.</summary>
    public const string Lost = "lost";

    /// <summary>Too many calls wait for the client.</summary>
    public const string Limit = "limit";

    internal static string Normalize(string? code) => code switch
    {
        NotSupported or PermissionDenied or NotEnabled or Timeout or InvalidArgument or Failed or Cancelled or NotReady or Lost or Limit => code,
        _ => Failed,
    };
}

/// <summary>An Essentials call that failed on the client, or never reached it.</summary>
/// <param name="code">What kind of failure (<see cref="EssentialsErrorCodes"/>).</param>
/// <param name="message">Why.</param>
public sealed class RouteableEssentialsException(string code, string message) : Exception(message)
{
    /// <summary>What kind of failure (<see cref="EssentialsErrorCodes"/>).</summary>
    public string Code { get; } = code;
}

/// <summary>How an Essentials call waits for the client.</summary>
public sealed class EssentialsCallOptions
{
    /// <summary><see cref="RouteablesOptions.EssentialsTimeout"/>, not replayed.</summary>
    public static EssentialsCallOptions Default { get; } = new();

    /// <summary>
    /// A call that waits for the user (a permission prompt, a file dialog, the camera): no timeout, it ends with the call, the session or
    /// <see cref="CancellationToken"/>.
    /// </summary>
    public static EssentialsCallOptions Interactive { get; } = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    /// <summary>
    /// A write the client can receive twice (a preference set): sent again to a client that resumed the session when the connection dropped
    /// before its answer, instead of failing as <see cref="EssentialsErrorCodes.Lost"/>.
    /// </summary>
    public static EssentialsCallOptions Replayable { get; } = new() { Replay = true };

    /// <summary>How long the client has to answer; <see cref="RouteablesOptions.EssentialsTimeout"/> when <see langword="null"/>, none when infinite.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Whether the call is sent again after a reconnection (idempotent calls only).</summary>
    public bool Replay { get; init; }

    /// <summary>
    /// Whether the call that runs (a command) is answered as going on while it waits for the client, as for a dialog: the client is then free
    /// to answer. <see langword="true"/> by default; a write nobody waits for does not need it.
    /// </summary>
    public bool Ask { get; init; } = true;
}
