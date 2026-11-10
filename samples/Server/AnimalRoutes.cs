using Routeables.Abstraction;
using Routeables.Abstraction.Registrations;
using Routeables.SignalR;
using XAnimals.ViewModels;
using IRouter = Routeables.Abstraction.Routing.IRouter;

namespace XAnimals.Server;

/// <summary>The "view" of a route on this server: nothing. The state of the view model is what the front end receives.</summary>
internal sealed class AnimalsPage;

/// <summary>
/// The detail of a panda on this server. <see cref="PandaDetailViewModel"/> receives the name from a property of the view
/// (<c>{panda::}</c>), and this server has no view, so the name comes by the constructor, like every other detail.
/// </summary>
[RouteableState(Settable = true)]
internal sealed class PandaDetail : PandaDetailViewModel
{
    public PandaDetail(IRouter router, string name) : base(router) => Name = name;
}

internal static class AnimalRoutes
{
    /// <summary>Registers the list of a species (<c>/cats</c>) and its detail (<c>/cats/{name}</c>).</summary>
    public static void RegisterAnimals<TList, TDetail>(this IServiceCollection services, string species)
        where TList : class
        where TDetail : class
    {
        services.RegisterRoute<AnimalsPage, TList>(route: $"/{species}");
        services.RegisterRoute<AnimalsPage, TDetail>(route: $"/{species}/{{name}}");
    }

    /// <summary>Registers a page the menu or a region opens by its name (<c>history</c>).</summary>
    public static void RegisterPage<TViewModel>(this IServiceCollection services, string route)
        where TViewModel : class
        => services.RegisterRoute<AnimalsPage, TViewModel>(route: route);
}
