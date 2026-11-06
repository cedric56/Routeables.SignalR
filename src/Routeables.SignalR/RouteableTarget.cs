namespace Routeables.SignalR;

/// <summary>A view model, besides the pages, that a client can watch under a name that starts with <c>$</c> (see <c>AddRouteableTarget</c>).</summary>
/// <param name="Name">The name of the target.</param>
/// <param name="ViewModel">The type of the view model; it is resolved from the services of the connection.</param>
public sealed record RouteableTarget(string Name, Type ViewModel);
