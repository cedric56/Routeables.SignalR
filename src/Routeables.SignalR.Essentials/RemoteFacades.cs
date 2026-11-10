using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
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

/// <summary>
/// The static MAUI facades (<c>Preferences.Default</c>, <c>Geolocation.Default</c>, <c>Connectivity.Current</c>…) of a Routeables.SignalR
/// server. They are process-wide: they never hold the service of a user. Each one forwards every call to the service of the session that runs
/// (<see cref="RouteableSession.Running"/>), the one the view models of that session also receive by injection.
/// </summary>
public static class RemoteFacades
{
    private static int _installed;

    /// <summary>Installs the forwarding facades of every Essentials service (once per process; <c>AddRouteablesEssentials</c> does it).</summary>
    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0)
            return;
        FacadeAccessors.SetAccelerometer(null, SessionFacade<IAccelerometer>.Create());
        FacadeAccessors.SetAppActions(null, SessionFacade<IAppActions>.Create());
        FacadeAccessors.SetAppInfo(null, SessionFacade<IAppInfo>.Create());
        FacadeAccessors.SetBarometer(null, SessionFacade<IBarometer>.Create());
        FacadeAccessors.SetBattery(null, SessionFacade<IBattery>.Create());
        FacadeAccessors.SetBrowser(null, SessionFacade<IBrowser>.Create());
        FacadeAccessors.SetClipboard(null, SessionFacade<IClipboard>.Create());
        FacadeAccessors.SetCompass(null, SessionFacade<ICompass>.Create());
        FacadeAccessors.SetConnectivity(null, SessionFacade<IConnectivity>.Create());
        FacadeAccessors.SetContacts(null, SessionFacade<IContacts>.Create());
        FacadeAccessors.SetDeviceDisplay(null, SessionFacade<IDeviceDisplay>.Create());
        FacadeAccessors.SetDeviceInfo(null, SessionFacade<IDeviceInfo>.Create());
        FacadeAccessors.SetEmail(null, SessionFacade<IEmail>.Create());
        FacadeAccessors.SetFilePicker(null, SessionFacade<IFilePicker>.Create());
        FacadeAccessors.SetFileSystem(null, SessionFacade<IFileSystem>.Create());
        FacadeAccessors.SetFlashlight(null, SessionFacade<IFlashlight>.Create());
        FacadeAccessors.SetGeocoding(null, SessionFacade<IGeocoding>.Create());
        FacadeAccessors.SetGeolocation(null, SessionFacade<IGeolocation>.Create());
        FacadeAccessors.SetGyroscope(null, SessionFacade<IGyroscope>.Create());
        FacadeAccessors.SetHapticFeedback(null, SessionFacade<IHapticFeedback>.Create());
        FacadeAccessors.SetLauncher(null, SessionFacade<ILauncher>.Create());
        FacadeAccessors.SetMagnetometer(null, SessionFacade<IMagnetometer>.Create());
        FacadeAccessors.SetMap(null, SessionFacade<IMap>.Create());
        FacadeAccessors.SetMediaPicker(null, SessionFacade<IMediaPicker>.Create());
        FacadeAccessors.SetOrientationSensor(null, SessionFacade<IOrientationSensor>.Create());
        FacadeAccessors.SetPhoneDialer(null, SessionFacade<IPhoneDialer>.Create());
        FacadeAccessors.SetPreferences(null, SessionFacade<IPreferences>.Create());
        FacadeAccessors.SetScreenshot(null, SessionFacade<IScreenshot>.Create());
        FacadeAccessors.SetSecureStorage(null, SessionFacade<ISecureStorage>.Create());
        FacadeAccessors.SetSemanticScreenReader(null, SessionFacade<ISemanticScreenReader>.Create());
        FacadeAccessors.SetShare(null, SessionFacade<IShare>.Create());
        FacadeAccessors.SetSms(null, SessionFacade<ISms>.Create());
        FacadeAccessors.SetTextToSpeech(null, SessionFacade<ITextToSpeech>.Create());
        FacadeAccessors.SetVersionTracking(null, SessionFacade<IVersionTracking>.Create());
        FacadeAccessors.SetVibration(null, SessionFacade<IVibration>.Create());
        FacadeAccessors.SetWebAuthenticator(null, SessionFacade<IWebAuthenticator>.Create());
    }

    /// <summary>The service <typeparamref name="T"/> of the session that runs; throws outside of a session.</summary>
    public static T Resolve<T>() where T : class
    {
        var session = RouteableSession.Running ?? throw new InvalidOperationException(
            $"{typeof(T).Name} was used outside of a Routeables session. The Essentials facades of a Routeables.SignalR server belong to the " +
            "session that runs (a command, an event of a view model, what they start); elsewhere (a hosted service, a timer created at startup) " +
            "there is no user to ask: inject the service in a view model instead.");
        return session.Services.GetRequiredService<T>();
    }
}

/// <summary>Forwards every member of <typeparamref name="T"/> (methods, properties, events) to the instance of the session that runs.</summary>
internal class SessionFacade<T> : DispatchProxy where T : class
{
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050", Justification = "A Routeables.SignalR server runs on the JIT.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "A Routeables.SignalR server is not trimmed.")]
    public static T Create() => Create<T, SessionFacade<T>>();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        var target = RemoteFacades.Resolve<T>();
        try
        {
            return targetMethod.Invoke(target, args);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }
}

/// <summary>The facades of Microsoft.Maui.Essentials only expose internal setters: they are reached as Essentieables does.</summary>
internal static class FacadeAccessors
{
    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "SetDefault")]
    public static extern void SetAccelerometer(
        [UnsafeAccessorType("Microsoft.Maui.Devices.Sensors.Accelerometer, Microsoft.Maui.Essentials")] object? facade, IAccelerometer? implementation);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "SetCurrent")]
    public static extern void SetAppActions(
        [UnsafeAccessorType("Microsoft.Maui.ApplicationModel.AppActions, Microsoft.Maui.Essentials")] object? facade, IAppActions? implementation);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "SetCurrent")]
    public static extern void SetAppInfo(
        [UnsafeAccessorType("Microsoft.Maui.ApplicationModel.AppInfo, Microsoft.Maui.Essentials")] object? facade, IAppInfo? implementation);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "SetDefault")]
    public static extern void SetBarometer(
        [UnsafeAccessorType("Microsoft.Maui.Devices.Sensors.Barometer, Microsoft.Maui.Essentials")] object? facade, IBarometer? implementation);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "SetDefault")]
    public static extern void SetBattery(
        [UnsafeAccessorType("Microsoft.Maui.Devices.Battery, Microsoft.Maui.Essentials")] object? facade, IBattery? implementation);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "SetDefault")]
    public static extern void SetBrowser(
        [UnsafeAccessorType("Microsoft.Maui.ApplicationModel.Browser, Microsoft.Maui.Essentials")] object? facade, IBrowser? implementation);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "SetDefault")]
    public static extern void SetClipboard(
        [UnsafeAccessorType("Microsoft.Maui.ApplicationModel.DataTransfer.Clipboard, Microsoft.Maui.Essentials")] object? facade, IClipboard? implementation);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "SetDefault")]
    public static extern void SetCompass(
        [UnsafeAccessorType("Microsoft.Maui.Devices.Sensors.Compass, Microsoft.Maui.Essentials")] object? facade, ICompass? implementation);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "SetCurrent")]
    public static extern void SetConnectivity(
        [UnsafeAccessorType("Microsoft.Maui.Networking.Connectivity, Microsoft.Maui.Essentials")] object? facade, IConnectivity? implementation);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "SetDefault")]
    public static extern void SetContacts(
        [UnsafeAccessorType("Microsoft.Maui.ApplicationModel.Communication.Contacts, Microsoft.Maui.Essentials")] object? facade, IContacts? implementation);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "SetCurrent")]
    public static extern void SetDeviceDisplay(
        [UnsafeAccessorType("Microsoft.Maui.Devices.DeviceDisplay, Microsoft.Maui.Essentials")] object? facade, IDeviceDisplay? implementation);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "SetCurrent")]
    public static extern void SetDeviceInfo(
        [UnsafeAccessorType("Microsoft.Maui.Devices.DeviceInfo, Microsoft.Maui.Essentials")] object? facade, IDeviceInfo? implementation);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "SetDefault")]
    public static extern void SetEmail(
        [UnsafeAccessorType("Microsoft.Maui.ApplicationModel.Communication.Email, Microsoft.Maui.Essentials")] object? facade, IEmail? implementation);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "SetDefault")]
    public static extern void SetFilePicker(
        [UnsafeAccessorType("Microsoft.Maui.Storage.FilePicker, Microsoft.Maui.Essentials")] object? facade, IFilePicker? implementation);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "SetCurrent")]
    public static extern void SetFileSystem(
        [UnsafeAccessorType("Microsoft.Maui.Storage.FileSystem, Microsoft.Maui.Essentials")] object? facade, IFileSystem? implementation);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "SetDefault")]
    public static extern void SetFlashlight(
        [UnsafeAccessorType("Microsoft.Maui.Devices.Flashlight, Microsoft.Maui.Essentials")] object? facade, IFlashlight? implementation);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "SetCurrent")]
    public static extern void SetGeocoding(
        [UnsafeAccessorType("Microsoft.Maui.Devices.Sensors.Geocoding, Microsoft.Maui.Essentials")] object? facade, IGeocoding? implementation);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "SetDefault")]
    public static extern void SetGeolocation(
        [UnsafeAccessorType("Microsoft.Maui.Devices.Sensors.Geolocation, Microsoft.Maui.Essentials")] object? facade, IGeolocation? implementation);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "SetDefault")]
    public static extern void SetGyroscope(
        [UnsafeAccessorType("Microsoft.Maui.Devices.Sensors.Gyroscope, Microsoft.Maui.Essentials")] object? facade, IGyroscope? implementation);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "SetDefault")]
    public static extern void SetHapticFeedback(
        [UnsafeAccessorType("Microsoft.Maui.Devices.HapticFeedback, Microsoft.Maui.Essentials")] object? facade, IHapticFeedback? implementation);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "SetDefault")]
    public static extern void SetLauncher(
        [UnsafeAccessorType("Microsoft.Maui.ApplicationModel.Launcher, Microsoft.Maui.Essentials")] object? facade, ILauncher? implementation);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "SetDefault")]
    public static extern void SetMagnetometer(
        [UnsafeAccessorType("Microsoft.Maui.Devices.Sensors.Magnetometer, Microsoft.Maui.Essentials")] object? facade, IMagnetometer? implementation);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "SetDefault")]
    public static extern void SetMap(
        [UnsafeAccessorType("Microsoft.Maui.ApplicationModel.Map, Microsoft.Maui.Essentials")] object? facade, IMap? implementation);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "SetDefault")]
    public static extern void SetMediaPicker(
        [UnsafeAccessorType("Microsoft.Maui.Media.MediaPicker, Microsoft.Maui.Essentials")] object? facade, IMediaPicker? implementation);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "SetDefault")]
    public static extern void SetOrientationSensor(
        [UnsafeAccessorType("Microsoft.Maui.Devices.Sensors.OrientationSensor, Microsoft.Maui.Essentials")] object? facade, IOrientationSensor? implementation);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "SetDefault")]
    public static extern void SetPhoneDialer(
        [UnsafeAccessorType("Microsoft.Maui.ApplicationModel.Communication.PhoneDialer, Microsoft.Maui.Essentials")] object? facade, IPhoneDialer? implementation);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "SetDefault")]
    public static extern void SetPreferences(
        [UnsafeAccessorType("Microsoft.Maui.Storage.Preferences, Microsoft.Maui.Essentials")] object? facade, IPreferences? implementation);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "SetDefault")]
    public static extern void SetScreenshot(
        [UnsafeAccessorType("Microsoft.Maui.Media.Screenshot, Microsoft.Maui.Essentials")] object? facade, IScreenshot? implementation);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "SetDefault")]
    public static extern void SetSecureStorage(
        [UnsafeAccessorType("Microsoft.Maui.Storage.SecureStorage, Microsoft.Maui.Essentials")] object? facade, ISecureStorage? implementation);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "SetDefault")]
    public static extern void SetSemanticScreenReader(
        [UnsafeAccessorType("Microsoft.Maui.Accessibility.SemanticScreenReader, Microsoft.Maui.Essentials")] object? facade, ISemanticScreenReader? implementation);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "SetDefault")]
    public static extern void SetShare(
        [UnsafeAccessorType("Microsoft.Maui.ApplicationModel.DataTransfer.Share, Microsoft.Maui.Essentials")] object? facade, IShare? implementation);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "SetDefault")]
    public static extern void SetSms(
        [UnsafeAccessorType("Microsoft.Maui.ApplicationModel.Communication.Sms, Microsoft.Maui.Essentials")] object? facade, ISms? implementation);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "SetDefault")]
    public static extern void SetTextToSpeech(
        [UnsafeAccessorType("Microsoft.Maui.Media.TextToSpeech, Microsoft.Maui.Essentials")] object? facade, ITextToSpeech? implementation);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "SetDefault")]
    public static extern void SetVersionTracking(
        [UnsafeAccessorType("Microsoft.Maui.ApplicationModel.VersionTracking, Microsoft.Maui.Essentials")] object? facade, IVersionTracking? implementation);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "SetDefault")]
    public static extern void SetVibration(
        [UnsafeAccessorType("Microsoft.Maui.Devices.Vibration, Microsoft.Maui.Essentials")] object? facade, IVibration? implementation);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "SetDefault")]
    public static extern void SetWebAuthenticator(
        [UnsafeAccessorType("Microsoft.Maui.Authentication.WebAuthenticator, Microsoft.Maui.Essentials")] object? facade, IWebAuthenticator? implementation);
}
