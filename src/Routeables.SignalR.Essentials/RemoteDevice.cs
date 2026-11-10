using System.Text.Json;
using Microsoft.Maui.Accessibility;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Devices;

namespace Routeables.SignalR.Essentials;

/// <summary>
/// <see cref="IAppInfo"/> of the client: the application the user runs (its name, version, theme). Service <c>appInfo</c>; state
/// <c>{ packageName, name, versionString, buildString, requestedTheme, requestedLayoutDirection, packagingModel }</c>; event <c>changed</c>
/// (same shape: the theme or the direction changed); method <c>showSettingsUI()</c>.
/// </summary>
public sealed class RemoteAppInfo : RemoteService, IAppInfo
{
    internal const string ServiceName = "appInfo";
    private JsonElement _state;

    /// <summary>Creates the app info of a session.</summary>
    public RemoteAppInfo(RouteableEssentials essentials) : base(essentials, ServiceName)
    {
        Subscribe("changed", payload => { if (payload is { ValueKind: JsonValueKind.Object } p) _state = p; });
        Initialize();
    }

    /// <inheritdoc />
    protected override void OnReady() => _state = TryGetState(out var state) ? state : default;

    /// <inheritdoc />
    public string PackageName => EssentialsJson.String(_state, "packageName", 256) ?? string.Empty;
    /// <inheritdoc />
    public string Name => EssentialsJson.String(_state, "name", 256) ?? string.Empty;
    /// <inheritdoc />
    public string VersionString => EssentialsJson.String(_state, "versionString", 64) ?? string.Empty;
    /// <inheritdoc />
    public Version Version => EssentialsJson.Version(VersionString);
    /// <inheritdoc />
    public string BuildString => EssentialsJson.String(_state, "buildString", 64) ?? string.Empty;
    /// <inheritdoc />
    public AppTheme RequestedTheme => EssentialsJson.Enum(_state, "requestedTheme", AppTheme.Unspecified);
    /// <inheritdoc />
    public AppPackagingModel PackagingModel => EssentialsJson.Enum(_state, "packagingModel", AppPackagingModel.Unpackaged);
    /// <inheritdoc />
    public LayoutDirection RequestedLayoutDirection => EssentialsJson.Enum(_state, "requestedLayoutDirection", LayoutDirection.Unknown);

    /// <inheritdoc />
    public void ShowSettingsUI() => Post("showSettingsUI");
}

/// <summary>
/// <see cref="IDeviceInfo"/> of the client. Service <c>deviceInfo</c>; state
/// <c>{ model, manufacturer, name, versionString, platform, idiom, deviceType }</c>.
/// </summary>
public sealed class RemoteDeviceInfo : RemoteService, IDeviceInfo
{
    internal const string ServiceName = "deviceInfo";
    private JsonElement _state;

    /// <summary>Creates the device info of a session.</summary>
    public RemoteDeviceInfo(RouteableEssentials essentials) : base(essentials, ServiceName) => Initialize();

    /// <inheritdoc />
    protected override void OnReady() => _state = TryGetState(out var state) ? state : default;

    /// <inheritdoc />
    public string Model => EssentialsJson.String(_state, "model", 256) ?? string.Empty;
    /// <inheritdoc />
    public string Manufacturer => EssentialsJson.String(_state, "manufacturer", 256) ?? string.Empty;
    /// <inheritdoc />
    public string Name => EssentialsJson.String(_state, "name", 256) ?? string.Empty;
    /// <inheritdoc />
    public string VersionString => EssentialsJson.String(_state, "versionString", 64) ?? string.Empty;
    /// <inheritdoc />
    public Version Version => EssentialsJson.Version(VersionString);
    /// <inheritdoc />
    public DevicePlatform Platform => EssentialsJson.String(_state, "platform", 64) is { Length: > 0 } p ? DevicePlatform.Create(p) : DevicePlatform.Unknown;
    /// <inheritdoc />
    public DeviceIdiom Idiom => EssentialsJson.String(_state, "idiom", 64) is { Length: > 0 } i ? DeviceIdiom.Create(i) : DeviceIdiom.Unknown;
    /// <inheritdoc />
    public DeviceType DeviceType => EssentialsJson.Enum(_state, "deviceType", DeviceType.Unknown);
}

/// <summary>
/// <see cref="IDeviceDisplay"/> of the client. Service <c>deviceDisplay</c>; state
/// <c>{ keepScreenOn, display: { width, height, density, orientation, rotation, refreshRate } }</c>; event <c>changed</c> (the display);
/// method <c>setKeepScreenOn(on)</c>.
/// </summary>
public sealed class RemoteDeviceDisplay : RemoteService, IDeviceDisplay
{
    internal const string ServiceName = "deviceDisplay";
    private DisplayInfo _display;
    private bool _keepScreenOn;

    /// <summary>Creates the display of a session.</summary>
    public RemoteDeviceDisplay(RouteableEssentials essentials) : base(essentials, ServiceName)
    {
        Subscribe("changed", payload =>
        {
            if (payload is not { ValueKind: JsonValueKind.Object } p)
                return;
            var next = Parse(p);
            if (next.Equals(_display))
                return;
            _display = next;
            MainDisplayInfoChanged?.Invoke(this, new DisplayInfoChangedEventArgs(next));
        });
        Initialize();
    }

    /// <inheritdoc />
    protected override void OnReady()
    {
        if (!TryGetState(out var state))
            return;
        if (state.TryGetProperty("display", out var display))
            _display = Parse(display);
        var announced = EssentialsJson.Bool(state, "keepScreenOn") ?? false;
        // A client that reconnected lost its wake lock: ask it again.
        if (_keepScreenOn && !announced)
            Post("setKeepScreenOn", [true]);
    }

    internal static DisplayInfo Parse(JsonElement value) => new(
        Math.Max(0, EssentialsJson.Double(value, "width") ?? 0),
        Math.Max(0, EssentialsJson.Double(value, "height") ?? 0),
        Math.Max(0, EssentialsJson.Double(value, "density") ?? 1),
        EssentialsJson.Enum(value, "orientation", DisplayOrientation.Unknown),
        EssentialsJson.Enum(value, "rotation", DisplayRotation.Unknown),
        (float)Math.Max(0, EssentialsJson.Double(value, "refreshRate") ?? 0));

    /// <inheritdoc />
    public bool KeepScreenOn
    {
        get => _keepScreenOn;
        set
        {
            if (_keepScreenOn == value)
                return;
            _keepScreenOn = value;
            Post("setKeepScreenOn", [value]);
        }
    }

    /// <inheritdoc />
    public DisplayInfo MainDisplayInfo => _display;

    /// <inheritdoc />
    public event EventHandler<DisplayInfoChangedEventArgs>? MainDisplayInfoChanged;
}

/// <summary>
/// <see cref="IBattery"/> of the client. Service <c>battery</c>; state and event <c>changed</c>:
/// <c>{ chargeLevel (0 to 1), state, powerSource, energySaverStatus }</c>.
/// </summary>
public sealed class RemoteBattery : RemoteService, IBattery
{
    internal const string ServiceName = "battery";
    private (double Level, BatteryState State, BatteryPowerSource Source, EnergySaverStatus Saver) _current = (1, BatteryState.Unknown, BatteryPowerSource.Unknown, EnergySaverStatus.Unknown);

    /// <summary>Creates the battery of a session.</summary>
    public RemoteBattery(RouteableEssentials essentials) : base(essentials, ServiceName)
    {
        Subscribe("changed", payload => { if (payload is { ValueKind: JsonValueKind.Object } p) Update(p, raise: true); });
        Initialize();
    }

    /// <inheritdoc />
    protected override void OnReady()
    {
        if (TryGetState(out var state))
            Update(state, raise: true);
    }

    private void Update(JsonElement value, bool raise)
    {
        var previous = _current;
        _current = (
            Math.Clamp(EssentialsJson.Double(value, "chargeLevel") ?? 1, 0, 1),
            EssentialsJson.Enum(value, "state", BatteryState.Unknown),
            EssentialsJson.Enum(value, "powerSource", BatteryPowerSource.Unknown),
            EssentialsJson.Enum(value, "energySaverStatus", EnergySaverStatus.Unknown));
        if (!raise)
            return;
        if (previous.Level != _current.Level || previous.State != _current.State || previous.Source != _current.Source)
            BatteryInfoChanged?.Invoke(this, new BatteryInfoChangedEventArgs(_current.Level, _current.State, _current.Source));
        if (previous.Saver != _current.Saver)
            EnergySaverStatusChanged?.Invoke(this, new EnergySaverStatusChangedEventArgs(_current.Saver));
    }

    /// <inheritdoc />
    public double ChargeLevel => _current.Level;
    /// <inheritdoc />
    public BatteryState State => _current.State;
    /// <inheritdoc />
    public BatteryPowerSource PowerSource => _current.Source;
    /// <inheritdoc />
    public EnergySaverStatus EnergySaverStatus => _current.Saver;
    /// <inheritdoc />
    public event EventHandler<BatteryInfoChangedEventArgs>? BatteryInfoChanged;
    /// <inheritdoc />
    public event EventHandler<EnergySaverStatusChangedEventArgs>? EnergySaverStatusChanged;
}

/// <summary><see cref="IVibration"/> of the client. Service <c>vibration</c>; state <c>{ isSupported }</c>; methods <c>vibrate(ms)</c>, <c>cancel()</c>.</summary>
public sealed class RemoteVibration : RemoteService, IVibration
{
    internal const string ServiceName = "vibration";
    private bool _supported;

    /// <summary>Creates the vibration of a session.</summary>
    public RemoteVibration(RouteableEssentials essentials) : base(essentials, ServiceName) => Initialize();

    /// <inheritdoc />
    protected override void OnReady() => _supported = TryGetState(out var s) && (EssentialsJson.Bool(s, "isSupported") ?? false);

    /// <inheritdoc />
    public bool IsSupported => _supported;

    /// <inheritdoc />
    public void Vibrate() => Vibrate(TimeSpan.FromMilliseconds(500));

    /// <inheritdoc />
    public void Vibrate(TimeSpan duration)
    {
        if (Essentials.HasAnnounced && !_supported)
            throw new FeatureNotSupportedException("The device of this user cannot vibrate.");
        // MAUI's bounds: at least one millisecond, at most five seconds.
        var ms = Math.Clamp(duration.TotalMilliseconds, 1, 5000);
        Post("vibrate", [ms]);
    }

    /// <inheritdoc />
    public void Cancel()
    {
        if (_supported)
            Post("cancel");
    }
}

/// <summary><see cref="IHapticFeedback"/> of the client. Service <c>hapticFeedback</c>; state <c>{ isSupported }</c>; method <c>perform(type)</c>.</summary>
public sealed class RemoteHapticFeedback : RemoteService, IHapticFeedback
{
    internal const string ServiceName = "hapticFeedback";
    private bool _supported;

    /// <summary>Creates the haptic feedback of a session.</summary>
    public RemoteHapticFeedback(RouteableEssentials essentials) : base(essentials, ServiceName) => Initialize();

    /// <inheritdoc />
    protected override void OnReady() => _supported = TryGetState(out var s) && (EssentialsJson.Bool(s, "isSupported") ?? false);

    /// <inheritdoc />
    public bool IsSupported => _supported;

    /// <inheritdoc />
    public void Perform(HapticFeedbackType type)
    {
        if (Essentials.HasAnnounced && !_supported)
            throw new FeatureNotSupportedException("The device of this user has no haptic feedback.");
        Post("perform", [type.ToString()]);
    }
}

/// <summary><see cref="IFlashlight"/> of the client. Service <c>flashlight</c>; methods <c>isSupported()</c>, <c>turnOn()</c>, <c>turnOff()</c>.</summary>
public sealed class RemoteFlashlight(RouteableEssentials essentials) : RemoteService(essentials, ServiceName), IFlashlight
{
    internal const string ServiceName = "flashlight";

    /// <inheritdoc />
    public async Task<bool> IsSupportedAsync()
    {
        if (!IsAvailable && Essentials.HasAnnounced)
            return false;
        return (await CallAsync("isSupported").ConfigureAwait(true)).ValueKind == JsonValueKind.True;
    }

    /// <inheritdoc />
    public Task TurnOnAsync() => CallAsync("turnOn", options: EssentialsCallOptions.Interactive);   // may ask for the camera

    /// <inheritdoc />
    public Task TurnOffAsync() => CallAsync("turnOff");
}

/// <summary><see cref="ISemanticScreenReader"/> of the client. Service <c>semanticScreenReader</c>; method <c>announce(text)</c>.</summary>
public sealed class RemoteSemanticScreenReader(RouteableEssentials essentials) : RemoteService(essentials, ServiceName), ISemanticScreenReader
{
    internal const string ServiceName = "semanticScreenReader";

    /// <inheritdoc />
    public void Announce(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Post("announce", [EssentialsJson.Limit(text, 4096)]);
    }
}
