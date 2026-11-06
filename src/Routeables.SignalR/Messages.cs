namespace Routeables.SignalR;

/// <summary>The targets a client can address: the page of the main router (<see cref="Page"/>), the modal, a region (named by its region), and the view models the application registered with <c>AddRouteableTarget</c> (named <c>$name</c>).</summary>
public static class RouteableTargets
{
    /// <summary>The page the main router shows.</summary>
    public const string Page = "";

    /// <summary>The view model of the modal dialog that is open.</summary>
    public const string Modal = "$modal";
}

/// <summary>
/// What the server tells the client. The names do not depend on SignalR: another transport carries the same messages.
/// </summary>
public interface IRouteableClient
{
    /// <summary>A page is now current: the client shows it and replaces everything it knew of the previous one.</summary>
    Task Navigated(RouteableNavigated message);

    /// <summary>A property of the current view model changed.</summary>
    Task Changed(RouteableChanged message);

    /// <summary>A route change failed.</summary>
    Task Failed(RouteableFailure message);

    /// <summary>A dialog the user has to answer: a message, a content dialog or a task dialog. The answer is <c>Answer</c> on the hub.</summary>
    Task DialogRequested(RouteableDialog message);

    /// <summary>A notification to show. <c>Dismiss</c> on the hub tells the server it was clicked or closed.</summary>
    Task Notified(RouteableToast message);

    /// <summary>A target has no page any more (the modal view model closed).</summary>
    Task Closed(RouteableClosed message);
}

/// <summary>A page became current.</summary>
/// <param name="Route">The address of the page.</param>
/// <param name="ViewModel">The name of the view model type, to choose the component that shows it.</param>
/// <param name="State">The properties of the view model, by name (camel case).</param>
/// <param name="Commands">The commands the client may call.</param>
/// <param name="Target">Where the page is shown: the main page, the modal, a region or a registered view model (see <see cref="RouteableTargets"/>).</param>
public sealed record RouteableNavigated(
    string Route,
    string ViewModel,
    IReadOnlyDictionary<string, object?> State,
    IReadOnlyList<string> Commands,
    string Target = RouteableTargets.Page);

/// <summary>A property of the current view model changed.</summary>
/// <param name="Property">The name of the property (camel case).</param>
/// <param name="Value">Its new value.</param>
/// <param name="Target">Where the page is shown: the main page, the modal, a region or a registered view model (see <see cref="RouteableTargets"/>).</param>
public sealed record RouteableChanged(string Property, object? Value, string Target = RouteableTargets.Page);

/// <summary>A target that has no view model any more.</summary>
public sealed record RouteableClosed(string Target);

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

/// <summary>A route change failed.</summary>
/// <param name="Route">The address asked for.</param>
/// <param name="Message">Why.</param>
public sealed record RouteableFailure(string Route, string Message);

/// <summary>The answer to a call of the client.</summary>
/// <param name="Success">Whether the call did what it was asked.</param>
/// <param name="Error">Why not, when it did not.</param>
public sealed record RouteableAck(bool Success, string? Error = null)
{
    /// <summary>The call succeeded.</summary>
    public static RouteableAck Ok { get; } = new(true);

    /// <summary>The call failed.</summary>
    public static RouteableAck Fail(string error) => new(false, error);
}
