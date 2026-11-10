using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Routeables.Abstraction;
using Routeables.Abstraction.Bases;
using Routeables.Abstraction.Guards;
using Routeables.Abstraction.Lifecycle;
using Routeables.Core;
using Routeables.Routing;
using Routeables.Segments;
using Routeables.Services;

namespace Routeables.SignalR;

/// <summary>Adds Routeables to an ASP.NET Core application that serves a JavaScript front end over SignalR.</summary>
public static class Extensions
{
    private const string ReflectionMessage = "Routeables.SignalR reads the view models by reflection: it does not support trimming nor Native AOT.";

    /// <summary>
    /// <c>AddRouteablesSignalR&lt;TMain&gt;</c> for an application that has no menu: no main view model to write.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">More registrations, such as the services the view models ask for.</param>
    /// <param name="options">The options (what crosses the network, the limits, the reconnection).</param>
    /// <exception cref="InvalidOperationException">The options are not valid (<see cref="RouteablesOptions.Problems"/>).</exception>
    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(ReflectionMessage)]
    public static IServiceCollection AddRouteablesSignalR(this IServiceCollection services, Action<IServiceCollection>? configure = null, Action<RouteablesOptions>? options = null)
        => services.AddRouteablesSignalR<DefaultMainViewModel>(configure, options);

    /// <summary>
    /// Registers SignalR and what a session needs: a router per connection (scoped), the sessions, the main view model, the authorization of the
    /// view models and the options. Register the routes of the application as usual (<c>AddGeneratedRoutes()</c>, or <c>RegisterRoute</c> by name).
    /// </summary>
    /// <typeparam name="TMain">The main view model; call <c>AddRouteablesSignalR()</c> without it for an application without a menu.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">More registrations, such as the services the view models ask for.</param>
    /// <param name="options">The options (what crosses the network, the limits, the reconnection).</param>
    /// <exception cref="InvalidOperationException">The options are not valid (<see cref="RouteablesOptions.Problems"/>).</exception>
    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(ReflectionMessage)]
    public static IServiceCollection AddRouteablesSignalR<TMain>(this IServiceCollection services, Action<IServiceCollection>? configure = null, Action<RouteablesOptions>? options = null)
        where TMain : class, IRouteableMenuViewModel
    {
        ArgumentNullException.ThrowIfNull(services);

        var settings = new RouteablesOptions();
        options?.Invoke(settings);
        settings.Validate();   // the application does not start with limits that make no sense
        services.AddSingleton(settings);
        services.AddSingleton(new RouteableSchema(settings));

        services.AddSignalR();
        services.AddAuthorizationCore();
        services.AddSingleton<RouteableSessions>();
        services.AddScoped<RouteableConnection>();
        services.RegisterMenuViewModel<TMain>(ServiceLifetime.Scoped);
        services.AddRouteCoordinator(ServiceLifetime.Scoped);
        services.AddScoped(_ => new RouteableHostLifetime(ServiceLifetime.Scoped));
        services.RegisterRouting(ServiceLifetime.Scoped);

        // The dialogs, the notifications and the modals go to the browser; an application that needs others registers them in configure.
        services.AddScoped<RemoteInteractions>();
        services.RegisterRouteableArgsFactory(() => RouteableArgs.Create());   // a modal asks for how the arguments of a route are created
        services.Replace(ServiceDescriptor.Scoped<IRouteableDialogService, RemoteDialogService>());
        services.Replace(ServiceDescriptor.Scoped<IRouteableNotificationService, RemoteNotificationService>());

        configure?.Invoke(services);

        // After configure, so that an application that replaces its own guard does not remove this one.
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IRouteGuard, RouteableAuthorizationGuard>());
        return services;
    }

    /// <summary>
    /// Offers a view model to the client besides the pages, under <paramref name="name"/> (a menu, the state of the shell). The client
    /// watches it with <c>Watch(name)</c>; the view model lives as long as the session. <c>[Authorize]</c> on its class applies.
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

    /// <summary>
    /// Maps the hub the client connects to. Protect it as any endpoint: <c>app.MapRouteables().RequireAuthorization()</c> (after
    /// <c>app.UseAuthentication()</c> and <c>app.UseAuthorization()</c>).
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="pattern">The address of the hub, <c>/routeables</c> by default.</param>
    /// <param name="options">
    /// The options of the connections, such as <c>CloseOnAuthenticationExpiration = true</c> (the connection closes when the cookie or the token
    /// of the user expires; the client reconnects with a new one).
    /// </param>
    public static HubEndpointConventionBuilder MapRouteables(this IEndpointRouteBuilder endpoints, string pattern = "/routeables", Action<HttpConnectionDispatcherOptions>? options = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints.MapHub<RouteablesHub>(pattern, options ?? (static _ => { }));
    }
}
