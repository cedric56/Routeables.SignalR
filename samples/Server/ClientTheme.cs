using System.ComponentModel;
using Routeables.Services;

namespace XAnimals.Server;

/// <summary>The theme a view model asked for (<see cref="IRouteableRequestedThemeService"/>), kept for the connection; the shell tells the app.</summary>
internal sealed class ClientTheme : IRouteableRequestedThemeService
{
    public RouteableRequestedTheme Requested { get; private set; }

    public event EventHandler? Changed;

    public void Request(RouteableRequestedTheme theme)
    {
        if (theme == Requested)
            return;

        Requested = theme;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
