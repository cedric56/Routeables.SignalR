using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Routeables.Abstraction;
using Routeables.Core;
using Routeables.Routing;
using Routeables.Segments;
using Routeables.Services;

namespace Routeables.SignalR;

/// <summary>Adds Routeables to an ASP.NET Core application that serves a JavaScript front end over SignalR.</summary>
public static class Extensions
{
    /// <summary>
    /// <c>AddRouteablesSignalR&lt;TMain&gt;</c> for an application that has no menu: no main view model to write.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">More registrations, such as the services the view models ask for.</param>
    public static IServiceCollection AddRouteablesSignalR(this IServiceCollection services, Action<IServiceCollection>? configure = null)
        => services.AddRouteablesSignalR<DefaultMainViewModel>(configure);

    /// <summary>
    /// Registers SignalR and what a session needs: a router per connection (scoped), the sessions, and the main view model.
    /// Register the routes of the application as usual (<c>AddGeneratedRoutes()</c>, or <c>RegisterRoute</c> by name).
    /// </summary>
    /// <typeparam name="TMain">The main view model; call <c>AddRouteablesSignalR()</c> without it for an application without a menu.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">More registrations, such as the services the view models ask for.</param>
    public static IServiceCollection AddRouteablesSignalR<TMain>(this IServiceCollection services, Action<IServiceCollection>? configure = null)
        where TMain : class, IMainViewModel
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSignalR();
        services.AddSingleton<RouteableSessions>();
        services.RegisterMainViewModel<TMain>(ServiceLifetime.Scoped);
        services.AddRouteCoordinator(ServiceLifetime.Scoped);
        services.AddScoped(_ => new RouteableHostLifetime(ServiceLifetime.Scoped));
        services.RegisterRouting(ServiceLifetime.Scoped);

        // The dialogs, the notifications and the modals go to the browser; an application that needs others registers them in configure.
        services.AddScoped<RemoteInteractions>();
        services.RegisterRouteableArgsFactory(() => RouteableArgs.Create());   // a modal asks for how the arguments of a route are created
        services.Replace(ServiceDescriptor.Scoped<IRouteableDialogService, RemoteDialogService>());
        services.Replace(ServiceDescriptor.Scoped<IRouteableNotificationService, RemoteNotificationService>());

        configure?.Invoke(services);
        return services;
    }

    /// <summary>
    /// Offers a view model to the client besides the pages, under <paramref name="name"/> (a menu, the state of the shell). The client
    /// watches it with <c>Watch(name)</c>; the view model lives as long as the connection.
    /// </summary>
    /// <typeparam name="TViewModel">The view model; it is created by the services of the connection.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="name">Its name, starting with <c>$</c>.</param>
    public static IServiceCollection AddRouteableTarget<TViewModel>(this IServiceCollection services, string name)
        where TViewModel : class
    {
        ArgumentNullException.ThrowIfNull(services);
        if (name is null || !name.StartsWith('$') || name.Length < 2 || name == RouteableTargets.Modal)
            throw new ArgumentException("The name of a target starts with '$' and is not the name of the modal.", nameof(name));

        services.TryAddScoped<TViewModel>();
        services.AddSingleton(new RouteableTarget(name, typeof(TViewModel)));
        return services;
    }

    /// <summary>Maps the hub the client connects to.</summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="pattern">The address of the hub, <c>/routeables</c> by default.</param>
    public static HubEndpointConventionBuilder MapRouteables(this IEndpointRouteBuilder endpoints, string pattern = "/routeables")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints.MapHub<RouteablesHub>(pattern);
    }
}
