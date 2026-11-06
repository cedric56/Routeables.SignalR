using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Routeables.Abstraction;
using Routeables.Services;
using Routeables.SignalR;
using XAnimals.Common;
using XAnimals.ViewModels;

namespace XAnimals.AngularServer;

internal static class Extensions
{
    /// <summary>
    /// Everything the Angular app needs besides <c>AddRouteablesSignalR</c>: the view models of the sample, their routes (no view on
    /// this side), the note modal, the theme, and the shell (the menu) the app watches as <c>$shell</c>, and the pets of the Adopt page (<c>$pets</c>).
    /// </summary>
    public static IServiceCollection AddAnimalsForAngular(this IServiceCollection services)
    {
        services.RegisterViewModels();

        // RegisterViewModels() makes the sign-in a singleton, as a desktop has one user. A connection is one user: each has its own.
        services.Replace(ServiceDescriptor.Scoped<MemberSession, MemberSession>());
        services.Replace(ServiceDescriptor.Scoped<IRouteGuard, MembersGuard>());

        // The theme the user asked for is kept for the connection; the shell tells the app, which draws itself in it.
        services.AddScoped<ClientTheme>();
        services.AddScoped<IRouteableRequestedThemeService>(p => p.GetRequiredService<ClientTheme>());
        services.AddRouteableTarget<ShellViewModel>("$shell");
        services.AddRouteableTarget<PetsViewModel>("$pets");

        // The routes: a view model alone. The "view" of every route is AnimalsPage, which is nothing.
        services.RegisterAnimals<CatsViewModel, CatDetailViewModel>("cats");
        services.RegisterAnimals<DogsViewModel, DogDetailViewModel>("dogs");
        services.RegisterAnimals<MonkeysViewModel, MonkeyDetailViewModel>("monkeys");
        services.RegisterAnimals<ElephantsViewModel, ElephantDetailViewModel>("elephants");
        services.RegisterAnimals<BearsViewModel, BearDetailViewModel>("bears");
        services.RegisterAnimals<PandasViewModel, PandaDetail>("pandas");
        services.RegisterPage<HistoryViewModel>("history");
        services.RegisterPage<RegionsViewModel>("regions");
        services.RegisterPage<AdoptViewModel>("adopt");
        services.RegisterPage<DialogsViewModel>("dialogs");
        services.RegisterPage<FeaturesViewModel>("features");
        services.RegisterPage<MembersViewModel>("members");
        services.RegisterPage<AboutViewModel>("about");
        services.RegisterPage<TabsViewModel>("tabs");

        // The pages that the Regions, History and Adopt pages send to their regions.
        services.RegisterPage<LayoutMenuViewModel>("layout-menu");
        services.RegisterPage<LayoutBarViewModel>("layout-bar");
        services.RegisterPage<LogoViewModel>("shell-logo");
        services.RegisterPage<PetViewModel>("pet");

        // The modal of the Dialogs page: found by the name of its view model.
        services.RegisterRoute<NoteModal, NoteViewModel>();
        return services;
    }
}
