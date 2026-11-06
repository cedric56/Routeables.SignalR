namespace Routeables.SignalR;

/// <summary>
/// Lets a browser client call this method of a view model. Only the methods that carry it can be called: a view model has public
/// methods that are not meant for a remote caller.
/// </summary>
/// <remarks>
/// The method returns <see langword="void"/>, <see cref="Task"/> or <see cref="Task{TResult}"/> (the result is not sent), and its parameters
/// are read from the JSON the client sends, in order. A parameter with a default value may be left out.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, Inherited = true)]
public sealed class RouteableCommandAttribute : Attribute
{
    /// <summary>The name the client uses. The name of the method when not set.</summary>
    public string? Name { get; set; }
}
