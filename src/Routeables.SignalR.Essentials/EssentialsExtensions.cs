using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Maui.Accessibility;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.ApplicationModel.Communication;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Authentication;
using Microsoft.Maui.Devices;
using Microsoft.Maui.Devices.Sensors;
using Microsoft.Maui.Media;
using Microsoft.Maui.Networking;
using Microsoft.Maui.Storage;

namespace Routeables.SignalR.Essentials;

/// <summary>Adds the .NET MAUI Essentials of the clients to a Routeables.SignalR server.</summary>
public static class EssentialsExtensions
{
    /// <summary>
    /// Registers every .NET MAUI Essentials service of the client of each session: one set per session, as the view models are; and installs
    /// the static facades (<c>Preferences.Default</c>, <c>Geolocation.Default</c>…) that forward to the session that runs. The services the
    /// application registered before are kept. Call it with <c>AddRouteablesSignalR</c>, and map <c>MapRouteablesEssentials()</c> for the
    /// services that move files (pickers, screenshot, share, launcher, email).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="options">The options (where the files go, their size).</param>
    /// <param name="installFacades">
    /// Whether to install the static facades. They are those of Essentieables.Facades, which a Blazor Server app using Essentieables shares:
    /// both hosts can run in one process, each call going to the session that runs. Leave them out only when the process sets the facades
    /// itself; injection keeps working.
    /// </param>
    public static IServiceCollection AddRouteablesEssentials(
        this IServiceCollection services,
        Action<RouteablesEssentialsOptions>? options = null,
        bool installFacades = true)
    {
        ArgumentNullException.ThrowIfNull(services);

        var settings = new RouteablesEssentialsOptions();
        options?.Invoke(settings);
        if (settings.MaxFileSize < 1 || settings.MaxFilesPerPick < 1 || settings.FileLinkLifetime <= TimeSpan.Zero
            || string.IsNullOrWhiteSpace(settings.Path) || !settings.Path.StartsWith('/'))
            throw new InvalidOperationException("The options of Routeables.SignalR.Essentials are not valid: MaxFileSize, MaxFilesPerPick and FileLinkLifetime must be positive, Path must start with '/'.");
        services.TryAddSingleton(settings);
        services.TryAddSingleton<RemoteFileLinks>();
        services.TryAddScoped(sp => new RemoteFiles(sp.GetRequiredService<RemoteFileLinks>(), sp.GetRequiredService<RouteablesEssentialsOptions>(), sp.GetService<RouteableConnection>()));
        services.TryAddScoped<RouteableEssentials>();

        static RouteableEssentials E(IServiceProvider sp) => sp.GetRequiredService<RouteableEssentials>();
        static RemoteFiles F(IServiceProvider sp) => sp.GetRequiredService<RemoteFiles>();

        services.TryAddScoped<IPreferences>(sp => new RemotePreferences(E(sp)));
        services.TryAddScoped<IConnectivity>(sp => new RemoteConnectivity(E(sp)));
        services.TryAddScoped<IAppInfo>(sp => new RemoteAppInfo(E(sp)));
        services.TryAddScoped<IDeviceInfo>(sp => new RemoteDeviceInfo(E(sp)));
        services.TryAddScoped<IDeviceDisplay>(sp => new RemoteDeviceDisplay(E(sp)));
        services.TryAddScoped<IBattery>(sp => new RemoteBattery(E(sp)));
        services.TryAddScoped<IVibration>(sp => new RemoteVibration(E(sp)));
        services.TryAddScoped<IHapticFeedback>(sp => new RemoteHapticFeedback(E(sp)));
        services.TryAddScoped<IFlashlight>(sp => new RemoteFlashlight(E(sp)));
        services.TryAddScoped<ISemanticScreenReader>(sp => new RemoteSemanticScreenReader(E(sp)));
        services.TryAddScoped<IPhoneDialer>(sp => new RemotePhoneDialer(E(sp)));
        services.TryAddScoped<ISms>(sp => new RemoteSms(E(sp)));
        services.TryAddScoped<IEmail>(sp => new RemoteEmail(E(sp), F(sp)));
        services.TryAddScoped<IContacts>(sp => new RemoteContacts(E(sp)));
        services.TryAddScoped<IBrowser>(sp => new RemoteBrowser(E(sp)));
        services.TryAddScoped<ILauncher>(sp => new RemoteLauncher(E(sp), F(sp)));
        services.TryAddScoped<IMap>(sp => new RemoteMap(E(sp)));
        services.TryAddScoped<IShare>(sp => new RemoteShare(E(sp), F(sp)));
        services.TryAddScoped<IClipboard>(sp => new RemoteClipboard(E(sp)));
        services.TryAddScoped<IGeolocation>(sp => new RemoteGeolocation(E(sp)));
        services.TryAddScoped<IGeocoding>(sp => new RemoteGeocoding(E(sp)));
        services.TryAddScoped<IAccelerometer>(sp => new RemoteAccelerometer(E(sp)));
        services.TryAddScoped<IBarometer>(sp => new RemoteBarometer(E(sp)));
        services.TryAddScoped<ICompass>(sp => new RemoteCompass(E(sp)));
        services.TryAddScoped<IGyroscope>(sp => new RemoteGyroscope(E(sp)));
        services.TryAddScoped<IMagnetometer>(sp => new RemoteMagnetometer(E(sp)));
        services.TryAddScoped<IOrientationSensor>(sp => new RemoteOrientationSensor(E(sp)));
        services.TryAddScoped<ITextToSpeech>(sp => new RemoteTextToSpeech(E(sp)));
        services.TryAddScoped<IFilePicker>(sp => new RemoteFilePicker(E(sp), F(sp)));
        services.TryAddScoped<IMediaPicker>(sp => new RemoteMediaPicker(E(sp), F(sp)));
        services.TryAddScoped<IScreenshot>(sp => new RemoteScreenshot(E(sp), F(sp)));
        services.TryAddScoped<ISecureStorage>(sp => new RemoteSecureStorage(E(sp)));
        services.TryAddScoped<IFileSystem>(sp => new RemoteFileSystem(F(sp), sp.GetService<IWebHostEnvironment>()));
        services.TryAddScoped<IVersionTracking>(sp => new RemoteVersionTracking(sp.GetRequiredService<IPreferences>(), sp.GetRequiredService<IAppInfo>()));
        services.TryAddScoped<IWebAuthenticator>(sp => new RemoteWebAuthenticator(E(sp)));
        services.TryAddScoped<IAppActions>(sp => new RemoteAppActions(E(sp)));

        if (installFacades)
            RemoteFacades.Install();
        return services;
    }

    /// <summary>
    /// The MAUI exception for an Essentials call that failed on the client: <see cref="FeatureNotSupportedException"/>,
    /// <see cref="PermissionException"/>, <see cref="FeatureNotEnabledException"/>, <see cref="TimeoutException"/>,
    /// <see cref="OperationCanceledException"/>, or <paramref name="exception"/> itself.
    /// </summary>
    public static Exception ToMauiException(this RouteableEssentialsException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.Code switch
        {
            EssentialsErrorCodes.NotSupported => new FeatureNotSupportedException(exception.Message),
            EssentialsErrorCodes.PermissionDenied => new PermissionException(exception.Message),
            EssentialsErrorCodes.NotEnabled => new FeatureNotEnabledException(exception.Message),
            EssentialsErrorCodes.Timeout or EssentialsErrorCodes.NotReady => new TimeoutException(exception.Message, exception),
            EssentialsErrorCodes.Cancelled => new OperationCanceledException(exception.Message, exception),
            _ => exception,
        };
    }
}
