using System.Diagnostics.CodeAnalysis;
using Essentieables;
using Microsoft.Extensions.DependencyInjection;

namespace Routeables.SignalR.Essentials;

/// <summary>
/// The static MAUI facades (<c>Preferences.Default</c>, <c>Geolocation.Default</c>, <c>Connectivity.Current</c>…) of a Routeables.SignalR
/// server. They are process-wide: they never hold the service of a user. Each one forwards every call to the service of the session that runs
/// (<see cref="RouteableSession.Running"/>), the one the view models of that session also receive by injection.
/// </summary>
/// <remarks>
/// The forwarding facades are those of Essentieables.Facades (<see cref="EssentialsFacades"/>): this host registers its session resolver
/// there, under <see cref="Host"/>. Another multi-user host of the same process (Blazor Server through Essentieables) registers its own, and
/// each call goes to the session that is running, whichever host owns it.
/// </remarks>
public static class RemoteFacades
{
    /// <summary>The name of this host among the session resolvers of <see cref="EssentialsFacades"/>.</summary>
    public const string Host = "Routeables";

    private static int _registered;

    /// <summary>
    /// Registers the Routeables session resolver (once per process) and installs the forwarding facades of every Essentials service
    /// (<c>AddRouteablesEssentials</c> does it). Installing again sets them again, and keeps the extended facades of another host.
    /// </summary>
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "A Routeables.SignalR server runs on the JIT.")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "A Routeables.SignalR server is not trimmed.")]
    public static void Install()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 0)
            EssentialsFacades.AddSessionResolver(Host, static () => RouteableSession.Running?.Services);
        EssentialsFacades.InstallForwarding();
    }

    /// <summary>The service <typeparamref name="T"/> of the Routeables session that runs; throws outside of a session.</summary>
    public static T Resolve<T>() where T : class
    {
        var session = RouteableSession.Running ?? throw new InvalidOperationException(
            $"{typeof(T).Name} was used outside of a Routeables session. The Essentials facades of a Routeables.SignalR server belong to the " +
            "session that runs (a command, an event of a view model, what they start); elsewhere (a hosted service, a timer created at startup) " +
            "there is no user to ask: inject the service in a view model instead.");
        return session.Services.GetRequiredService<T>();
    }
}
