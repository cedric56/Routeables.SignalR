namespace Routeables.SignalR;

/// <summary>
/// Sends a property of a view model to the client (<see cref="RouteableExposure.Explicit"/>, the default). On a class, every public property of
/// the class is sent. A property that is not marked stays on the server.
/// </summary>
/// <example>
/// <code>
/// public sealed class CustomerViewModel
/// {
///     [RouteableState] public string Name { get; private set; }
///     [RouteableState(Settable = true)] public string Search { get; set; }   // a text box
///     public string InternalToken { get; set; }                            // never sent, never set
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Class | AttributeTargets.Struct, Inherited = true, AllowMultiple = false)]
public sealed class RouteableStateAttribute : Attribute
{
    /// <summary>
    /// Whether the client may set the property (<c>Set</c>, <c>SetOn</c>): it needs a public setter too. On a class, every property of the class that
    /// has a public setter. <see langword="false"/> by default: the client reads, the commands write.
    /// </summary>
    public bool Settable
    {
        get => _settable;
        set
        {
            _settable = value;
            SettableIsSet = true;
        }
    }

    private bool _settable;

    /// <summary>
    /// Whether <see cref="Settable"/> was given. On a property, a given value wins over the class: <c>[RouteableState(Settable = false)]</c> keeps
    /// one property of a class marked <c>[RouteableState(Settable = true)]</c> out of the reach of the client.
    /// </summary>
    public bool SettableIsSet { get; private set; }
}
