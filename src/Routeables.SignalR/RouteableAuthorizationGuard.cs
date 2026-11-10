using Microsoft.Extensions.DependencyInjection;
using Routeables.Abstraction;
using Routeables.Abstraction.Guards;
using Routeables.Abstraction.Routing;

namespace Routeables.SignalR;

/// <summary>
/// The route guard that <c>AddRouteablesSignalR</c> registers: a route change to a view model whose class carries <c>[Authorize]</c> is cancelled
/// when the user of the connection does not meet it, whoever asked for it (the client, or a command that navigates).
/// </summary>
/// <remarks>
/// It spares the user a page he may not see; it is not the only barrier: a view model the user may not see is never sent, whatever showed it.
/// Register it again if the application replaces the guards (<c>services.Replace(...IRouteGuard...)</c> removes it).
/// </remarks>
public sealed class RouteableAuthorizationGuard(IServiceProvider services) : IRouteGuard
{
    private RouteableAuthorizer? _authorizer;

    /// <summary>Before the guards of the application.</summary>
    public int Order => int.MinValue;

    /// <inheritdoc />
    public async Task<RouteGuardDecision> CanEnterAsync(RouteableRoutingEventArgs change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.To?.ViewModel is not { } viewModel)
            return RouteGuardDecision.Allow;

        var schema = services.GetService<RouteableSchema>();
        if (schema is null)
            return RouteGuardDecision.Allow;

        _authorizer ??= new RouteableAuthorizer(services, services.GetService<RouteableConnection>() ?? new RouteableConnection());
        return await _authorizer.CanSeeAsync(viewModel, schema.Shape(viewModel.GetType())).ConfigureAwait(true)
            ? RouteGuardDecision.Allow
            : RouteGuardDecision.Cancel;
    }
}
