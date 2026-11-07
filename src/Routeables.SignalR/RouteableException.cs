namespace Routeables.SignalR;

/// <summary>
/// An error meant for the visitor: a view model throws it from a command or a setter and its message reaches the client, whatever
/// <see cref="RouteablesOptions.DetailedErrors"/> says. Any other exception is logged and reaches the client as a generic message, unless detailed errors are on.
/// </summary>
public class RouteableException : Exception
{
    /// <summary>Creates the error.</summary>
    public RouteableException()
    {
    }

    /// <summary>Creates the error with the message the visitor reads.</summary>
    public RouteableException(string message) : base(message)
    {
    }

    /// <summary>Creates the error with the message the visitor reads and the exception that caused it (not sent).</summary>
    public RouteableException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>The codes of a failed <see cref="RouteableAck"/>, <see cref="RouteableCompleted"/> or <see cref="RouteableFailure"/>.</summary>
public static class RouteableErrorCodes
{
    /// <summary>What the call names does not exist (a target, a command, a property, a dialog).</summary>
    public const string NotFound = "not-found";

    /// <summary>The user may not do it.</summary>
    public const string Unauthorized = "unauthorized";

    /// <summary>An argument is missing, of the wrong type, or too large.</summary>
    public const string InvalidArgument = "invalid-argument";

    /// <summary>The command, the setter or the route change failed.</summary>
    public const string Failed = "failed";

    /// <summary>The command was cancelled.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>A limit of <see cref="RouteablesOptions"/> was reached.</summary>
    public const string Limit = "limit";

    /// <summary>The command cannot run now (<see cref="System.Windows.Input.ICommand.CanExecute"/>).</summary>
    public const string Disabled = "disabled";

    /// <summary>The client and the server do not speak the same protocol.</summary>
    public const string Protocol = "protocol";
}
