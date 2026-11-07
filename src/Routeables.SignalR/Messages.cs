namespace Routeables.SignalR;

/// <summary>The targets a client can address: the page of the main router (<see cref="Page"/>), the modal, a region (named by its region), and the view models the application registered with <c>AddRouteableTarget</c> (named <c>$name</c>).</summary>
public static class RouteableTargets
{
    /// <summary>The page the main router shows.</summary>
    public const string Page = "";

    /// <summary>The view model of the modal dialog that is open.</summary>
    public const string Modal = "$modal";
}

/// <summary>The version of the messages the client and the server exchange. A client says which one it speaks (<c>Hello</c>).</summary>
public static class RouteableProtocol
{
    /// <summary>
    /// 2: every target is an ordered stream (a generation per page, a version per change), changes are patches, a long call ends with
    /// <see cref="IRouteableClient.Completed"/>, and a session can be resumed after a reconnection.
    /// </summary>
    public const int Version = 2;
}

/// <summary>
/// What the server tells the client. The names do not depend on SignalR: another transport carries the same messages.
/// </summary>
/// <remarks>
/// The messages of a connection arrive in the order they were told, and the answer to a call arrives after the messages the call caused.
/// For one target the stream is: <see cref="Navigated"/> (a full state, a new generation, version 0), then <see cref="Changed"/> (same
/// generation, version 1, 2, …), until the next <see cref="Navigated"/> or <see cref="Closed"/>. A client drops what belongs to an older
/// generation, and asks the state again (<c>Resync</c>) when a version is missing.
/// </remarks>
public interface IRouteableClient
{
    /// <summary>A page is now current (or its state is sent again): the client shows it and replaces everything it knew of the previous one.</summary>
    Task Navigated(RouteableNavigated message);

    /// <summary>The state of the current view model of a target changed.</summary>
    Task Changed(RouteableChanged message);

    /// <summary>Something failed that no call is waiting for: a route change, a part of the state that cannot be sent.</summary>
    Task Failed(RouteableFailure message);

    /// <summary>A dialog the user has to answer: a message, a content dialog or a task dialog. The answer is <c>Answer</c> on the hub.</summary>
    Task DialogRequested(RouteableDialog message);

    /// <summary>A notification to show. <c>Dismiss</c> on the hub tells the server it was clicked or closed.</summary>
    Task Notified(RouteableToast message);

    /// <summary>A target has no page any more (the modal view model closed, a page the user may not see).</summary>
    Task Closed(RouteableClosed message);

    /// <summary>A call that was answered <see cref="RouteableAck.Pending"/> has ended.</summary>
    Task Completed(RouteableCompleted message);
}

/// <summary>A page became current, or its full state is sent again.</summary>
/// <param name="Route">The address of the page.</param>
/// <param name="ViewModel">The name of the view model type, to choose the component that shows it.</param>
/// <param name="State">The properties of the view model, by name (camel case).</param>
/// <param name="Commands">The commands the client may call.</param>
/// <param name="Target">Where the page is shown: the main page, the modal, a region or a registered view model (see <see cref="RouteableTargets"/>).</param>
/// <param name="Generation">Grows at every message of this kind for the target; the <see cref="RouteableChanged"/> that follow carry it.</param>
/// <param name="Instance">Identifies the view model object: the same value when the state of the same page is sent again (a resync).</param>
public sealed record RouteableNavigated(
    string Route,
    string ViewModel,
    IReadOnlyDictionary<string, object?> State,
    IReadOnlyList<string> Commands,
    string Target = RouteableTargets.Page,
    long Generation = 0,
    long Instance = 0);

/// <summary>Changes of the state of the view model a target shows, to apply in order.</summary>
/// <param name="Target">The target.</param>
/// <param name="Generation">The generation of the page they belong to.</param>
/// <param name="Version">1 for the first message after <see cref="RouteableNavigated"/>, then one more at each message.</param>
/// <param name="Patches">The changes.</param>
public sealed record RouteableChanged(string Target, long Generation, long Version, IReadOnlyList<RouteablePatch> Patches);

/// <summary>
/// One change of a state. <c>set</c>: the value at <see cref="Path"/> is <see cref="Value"/>. <c>splice</c>: in the array at <see cref="Path"/>,
/// <see cref="Remove"/> items are removed at <see cref="Index"/> and the items of <see cref="Value"/> (an array) inserted there.
/// </summary>
/// <param name="Op"><c>set</c> or <c>splice</c>.</param>
/// <param name="Path">Property names and indexes from the state: <c>["title"]</c>, <c>["items", 3, "name"]</c>.</param>
/// <param name="Value">The new value (<c>set</c>), or the items to insert (<c>splice</c>).</param>
/// <param name="Index">Where the items are removed and inserted (<c>splice</c>).</param>
/// <param name="Remove">How many items are removed (<c>splice</c>).</param>
public sealed record RouteablePatch(string Op, IReadOnlyList<object> Path, object? Value, int? Index = null, int? Remove = null)
{
    /// <summary>The kind of patch that replaces a value.</summary>
    public const string Set = "set";

    /// <summary>The kind of patch that removes and inserts items of an array.</summary>
    public const string Splice = "splice";
}

/// <summary>A target that has no view model any more.</summary>
/// <param name="Target">The target.</param>
/// <param name="Generation">The generation that ends: what the target showed before.</param>
public sealed record RouteableClosed(string Target, long Generation = 0);

/// <summary>
/// A dialog. <see cref="Buttons"/> are the texts of the buttons; the answer is the index of the one that was chosen.
/// </summary>
public sealed record RouteableDialog(
    string Id,
    string Kind,
    string Title,
    string? Header,
    string? Body,
    string? Footer,
    bool ShowProgress,
    IReadOnlyList<string> Buttons);

/// <summary>A notification. The client closes it after <see cref="ExpirationSeconds"/> when it has one.</summary>
public sealed record RouteableToast(int Id, string Title, string Message, string Type, double? ExpirationSeconds);

/// <summary>Something failed that no call waits for.</summary>
/// <param name="Route">The address asked for, when a route change failed; empty otherwise.</param>
/// <param name="Message">Why.</param>
/// <param name="Target">The target it concerns, when there is one.</param>
/// <param name="Code">What kind of failure (<see cref="RouteableErrorCodes"/>).</param>
public sealed record RouteableFailure(string Route, string Message, string? Target = null, string? Code = null);

/// <summary>The answer to a call of the client.</summary>
/// <param name="Success">Whether the call did what it was asked (so far, when <see cref="Pending"/>).</param>
/// <param name="Error">Why not, when it did not.</param>
/// <param name="Code">What kind of failure (<see cref="RouteableErrorCodes"/>).</param>
/// <param name="Operation">The call that goes on, when <see cref="Pending"/>: <see cref="IRouteableClient.Completed"/> names it when it ends.</param>
/// <param name="Pending">The call did not end within <see cref="RouteablesOptions.CommandGrace"/>, or it asks the visitor something.</param>
public sealed record RouteableAck(bool Success, string? Error = null, string? Code = null, string? Operation = null, bool Pending = false)
{
    /// <summary>The call succeeded.</summary>
    public static RouteableAck Ok { get; } = new(true);

    /// <summary>The call failed.</summary>
    public static RouteableAck Fail(string error, string? code = RouteableErrorCodes.Failed) => new(false, error, code);

    /// <summary>The call goes on.</summary>
    public static RouteableAck Running(string operation) => new(true, null, null, operation, true);
}

/// <summary>A call that went on has ended.</summary>
/// <param name="Operation">The <see cref="RouteableAck.Operation"/> of the call.</param>
/// <param name="Success">Whether it succeeded.</param>
/// <param name="Error">Why not.</param>
/// <param name="Code">What kind of failure (<see cref="RouteableErrorCodes"/>).</param>
public sealed record RouteableCompleted(string Operation, bool Success, string? Error = null, string? Code = null);

/// <summary>What a client says first, and again after each reconnection.</summary>
/// <param name="Protocol">The protocol it speaks (<see cref="RouteableProtocol.Version"/>).</param>
/// <param name="Resume">The <see cref="RouteableWelcome.Session"/> of the connection that dropped, to get its view models back.</param>
public sealed record RouteableHello(int Protocol, string? Resume = null);

/// <summary>The answer to <see cref="RouteableHello"/>.</summary>
/// <param name="Success">Whether the client may go on (it speaks the protocol of the server).</param>
/// <param name="Protocol">The protocol of the server.</param>
/// <param name="Session">The token that resumes this session after a reconnection; keep it secret.</param>
/// <param name="Resumed">Whether the session of <see cref="RouteableHello.Resume"/> was resumed: its targets are sent again. Otherwise the session is new.</param>
/// <param name="Error">Why the client may not go on.</param>
public sealed record RouteableWelcome(bool Success, int Protocol, string? Session, bool Resumed, string? Error = null);
