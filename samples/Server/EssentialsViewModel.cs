using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using Microsoft.Maui.Accessibility;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.ApplicationModel.Communication;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Devices;
using Microsoft.Maui.Devices.Sensors;
using Microsoft.Maui.Media;
using Microsoft.Maui.Networking;
using Microsoft.Maui.Storage;
using Routeables.SignalR;
using Routeables.SignalR.Essentials;
using XAnimals.Common;

namespace XAnimals.Server;

/// <summary>
/// One device API of the Essentials page: what it is, whether the client announced it, and the result of its last test. A test runs on the
/// server, in the view model, with the .NET MAUI contract: the call reaches the device of the user of the session.
/// </summary>
[RouteableState]
internal sealed class EssentialsCheck : INotifyPropertyChanged
{
    private readonly Func<EssentialsCheck, Task<string>> _run;
    private string _status = "ready";
    private string _result = string.Empty;
    private bool _supported;
    private bool _active;

    public EssentialsCheck(string group, string name, string? service, string description, string action, Func<EssentialsCheck, Task<string>> run, string? stopAction = null)
    {
        Group = group;
        Name = name;
        Service = service ?? string.Empty;
        Description = description;
        Action = action;
        StopAction = stopAction ?? string.Empty;
        _run = run;
        RunCommand = new AsyncCommand(RunAsync);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The family of the API (Device, Storage, Communication, Location, Sensors, Media).</summary>
    public string Group { get; }

    /// <summary>The name of the test.</summary>
    public string Name { get; }

    /// <summary>The name of the service in the protocol (<c>preferences</c>, <c>geolocation</c>); empty for what runs on the server alone.</summary>
    public string Service { get; }

    /// <summary>What the test does, in one line.</summary>
    public string Description { get; }

    /// <summary>The label of the button.</summary>
    public string Action { get; }

    /// <summary>The label of the button while the test is active (a sensor that streams), empty for a test that ends.</summary>
    public string StopAction { get; }

    /// <summary>The label to show now.</summary>
    public string Label => Active && StopAction.Length > 0 ? StopAction : Action;

    /// <summary>Whether the client announced the service (always true for what runs on the server).</summary>
    public bool Supported { get => _supported; set => Set(ref _supported, value, nameof(Supported)); }

    /// <summary>ready, running, ok, failed, unsupported, denied (a permission), cancelled.</summary>
    public string Status { get => _status; set => Set(ref _status, value, nameof(Status)); }

    /// <summary>What the test gave, or why it failed.</summary>
    public string Result { get => _result; set => Set(ref _result, value, nameof(Result)); }

    /// <summary>Whether a streaming test (a sensor, the location) is running.</summary>
    public bool Active
    {
        get => _active;
        set
        {
            if (Set(ref _active, value, nameof(Active)))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
        }
    }

    /// <summary>Runs the test (or starts and stops it).</summary>
    public ICommand RunCommand { get; }

    private async Task RunAsync()
    {
        Status = "running";
        try
        {
            Result = await _run(this);
            Status = "ok";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Active = false;
            (Status, Result) = Describe(exception);
        }
    }

    /// <summary>The status and the text of a failure: the MAUI exception of a refused permission, a missing API…</summary>
    public static (string Status, string Message) Describe(Exception exception) => exception switch
    {
        FeatureNotSupportedException => ("unsupported", exception.Message),
        FeatureNotEnabledException => ("failed", "Not enabled on the device: " + exception.Message),
        PermissionException => ("denied", exception.Message),
        TaskCanceledException or OperationCanceledException => ("cancelled", "Cancelled."),
        RouteableEssentialsException essentials => (essentials.Code switch
        {
            "not-supported" => "unsupported",
            "permission-denied" => "denied",
            "cancelled" => "cancelled",
            _ => "failed",
        }, essentials.Message),
        _ => ("failed", exception.Message),
    };

    private bool Set<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}

/// <summary>
/// The Essentials page: every .NET MAUI Essentials contract, tested from the server on the device of the user (Routeables.SignalR.Essentials).
/// The same page runs in Angular, Vue and React (the browser, <c>browserEssentials()</c>) and in Flutter (the services of the Flutter sample):
/// each client announces what it can do, and the page says what it got.
/// </summary>
[RouteableState]
internal sealed class EssentialsViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly RouteableEssentials _essentials;
    private readonly List<Action> _unsubscribe = [];

    public EssentialsViewModel(RouteableEssentials essentials, IServiceProvider services)
    {
        _essentials = essentials;
        T Get<T>() where T : notnull => services.GetRequiredService<T>();

        var app = Get<IAppInfo>();
        var device = Get<IDeviceInfo>();
        var display = Get<IDeviceDisplay>();
        var battery = Get<IBattery>();
        var connectivity = Get<IConnectivity>();
        var versions = Get<IVersionTracking>();
        var preferences = Get<IPreferences>();
        var secure = Get<ISecureStorage>();
        var files = Get<IFileSystem>();
        var clipboard = Get<IClipboard>();
        var share = Get<IShare>();
        var email = Get<IEmail>();
        var sms = Get<ISms>();
        var dialer = Get<IPhoneDialer>();
        var browser = Get<IBrowser>();
        var launcher = Get<ILauncher>();
        var map = Get<IMap>();
        var contacts = Get<IContacts>();
        var geolocation = Get<IGeolocation>();
        var geocoding = Get<IGeocoding>();
        var accelerometer = Get<IAccelerometer>();
        var gyroscope = Get<IGyroscope>();
        var magnetometer = Get<IMagnetometer>();
        var compass = Get<ICompass>();
        var barometer = Get<IBarometer>();
        var orientation = Get<IOrientationSensor>();
        var vibration = Get<IVibration>();
        var haptic = Get<IHapticFeedback>();
        var flashlight = Get<IFlashlight>();
        var reader = Get<ISemanticScreenReader>();
        var speech = Get<ITextToSpeech>();
        var picker = Get<IFilePicker>();
        var media = Get<IMediaPicker>();
        var screenshot = Get<IScreenshot>();

        static string N(double value, string format = "0.00") => value.ToString(format, CultureInfo.InvariantCulture);
        static string Time() => DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        string AppText() => $"{app.Name} {app.VersionString} ({app.BuildString}) · {app.PackageName} · theme {app.RequestedTheme}";
        string DisplayText(DisplayInfo d) => $"{N(d.Width, "0")}×{N(d.Height, "0")} at {N(d.Density, "0.##")}x · {d.Orientation} · {d.Rotation}";
        string BatteryText() => $"{battery.ChargeLevel.ToString("P0", CultureInfo.InvariantCulture)} · {battery.State} · {battery.PowerSource} · saver {battery.EnergySaverStatus}";
        string NetworkText() => $"{connectivity.NetworkAccess} · {(connectivity.ConnectionProfiles.Any() ? string.Join(", ", connectivity.ConnectionProfiles) : "no profile")}";
        static string Where(Location l) => $"{N(l.Latitude, "0.00000")}, {N(l.Longitude, "0.00000")} (±{N(l.Accuracy ?? 0, "0")} m)";

        // A sensor: started and stopped by the same button; its readings are the result, as they come.
        EssentialsCheck Sensor<TArgs>(string name, string service, string description, ISensorLike sensor, Action<EventHandler<TArgs>> add, Action<EventHandler<TArgs>> remove, Func<TArgs, string> reading)
        {
            EssentialsCheck? check = null;
            var shown = DateTime.MinValue;
            void OnReading(object? sender, TArgs e)
            {
                // The readings come at most 20 times a second; the page shows 5 of them: the list of the tests is sent whole at each change.
                var now = DateTime.UtcNow;
                if (check is null || now - shown < TimeSpan.FromMilliseconds(200))
                    return;
                shown = now;
                check.Result = reading(e);
            }
            check = new EssentialsCheck("Sensors", name, service, description, "Start", c =>
            {
                if (sensor.IsMonitoring)
                {
                    sensor.Stop();
                    remove(OnReading);
                    c.Active = false;
                    return Task.FromResult($"Stopped at {Time()}.");
                }
                add(OnReading);
                sensor.Start(SensorSpeed.UI);
                c.Active = true;
                return Task.FromResult("Waiting for a reading…");
            }, "Stop");
            _unsubscribe.Add(() =>
            {
                remove(OnReading);
                if (sensor.IsMonitoring)
                    sensor.Stop();
            });
            return check;
        }

        var flashlightOn = false;
        Location? lastPlace = null;

        Checks =
        [
            // ---------------------------------------------------------------- device
            new("Device", "App", "appInfo", "IAppInfo: what the client says of the application.", "Read", _ => Task.FromResult(AppText())),
            new("Device", "Device", "deviceInfo", "IDeviceInfo: the model, the system, the kind of device.", "Read",
                _ => Task.FromResult($"{device.Manufacturer} {device.Model} · {device.Name} {device.VersionString} · {device.Platform} · {device.Idiom} · {device.DeviceType}")),
            new("Device", "Display", "deviceDisplay", "IDeviceDisplay: the size of the screen; follows its changes (turn the device, resize the window).", "Read",
                _ => Task.FromResult(DisplayText(display.MainDisplayInfo))),
            new("Device", "Keep the screen on", "deviceDisplay", "IDeviceDisplay.KeepScreenOn: the screen stays on while the page is shown.", "Turn on", c =>
            {
                display.KeepScreenOn = !display.KeepScreenOn;
                c.Active = display.KeepScreenOn;
                return Task.FromResult(display.KeepScreenOn ? "The screen stays on." : "The screen may turn off.");
            }, "Turn off"),
            new("Device", "Battery", "battery", "IBattery: the charge and the power source; follows their changes.", "Read", _ => Task.FromResult(BatteryText())),
            new("Device", "Network", "connectivity", "IConnectivity: the access and the profiles; follows their changes (go offline).", "Read", _ => Task.FromResult(NetworkText())),
            new("Device", "Versions", "preferences", "IVersionTracking: computed on the server, as MAUI does, in the preferences of the client.", "Read", _ =>
            {
                versions.Track();
                return Task.FromResult($"{(versions.IsFirstLaunchEver ? "first launch ever" : "launched before")} · current {versions.CurrentVersion} · history {string.Join(", ", versions.VersionHistory)}");
            }),

            // ---------------------------------------------------------------- storage
            new("Storage", "Preferences", "preferences", "IPreferences: a counter kept on this device (reload the page: it goes on).", "Count a visit", _ =>
            {
                var visits = preferences.Get("essentials.visits", 0) + 1;
                preferences.Set("essentials.visits", visits);
                return Task.FromResult($"Visit {visits} of this page on this device.");
            }),
            new("Storage", "Secure storage", "secureStorage", "ISecureStorage: a secret written then read back.", "Write and read", async _ =>
            {
                var secret = "secret " + Time();
                await secure.SetAsync("essentials.secret", secret);
                return $"Wrote \"{secret}\", read \"{await secure.GetAsync("essentials.secret")}\".";
            }),
            new("Storage", "File system", null, "IFileSystem: a file in the cache directory of the session, on the server.", "Write a file", async _ =>
            {
                var path = Path.Combine(files.CacheDirectory, "essentials.txt");
                await File.WriteAllTextAsync(path, "Written at " + Time());
                return $"{new FileInfo(path).Length} bytes in the cache of the session.";
            }),

            // ---------------------------------------------------------------- communication
            new("Communication", "Clipboard", "clipboard", "IClipboard: a text copied, then read back.", "Copy and paste", async _ =>
            {
                var text = "Copied by the server at " + Time();
                await clipboard.SetTextAsync(text);
                return $"Read back: \"{await clipboard.GetTextAsync()}\".";
            }),
            new("Communication", "Share", "share", "IShare: the share sheet of the device, with a text and a link.", "Share", async _ =>
            {
                await share.RequestAsync(new ShareTextRequest { Title = "Routeables", Text = "Shared by a Routeables.SignalR server", Uri = "https://github.com/cedric56/Routeables.SignalR" });
                return "The share sheet was shown.";
            }),
            new("Communication", "Email", "email", "IEmail: a message to compose in the mail app.", "Compose", async _ =>
            {
                await email.ComposeAsync(new EmailMessage("Routeables", "Composed by a Routeables.SignalR server.", "someone@example.com"));
                return "The mail app was opened.";
            }),
            new("Communication", "SMS", "sms", "ISms: a text to compose in the messages app.", "Compose", async _ =>
            {
                await sms.ComposeAsync(new SmsMessage("Composed by a Routeables.SignalR server.", "+33600000000"));
                return "The messages app was opened.";
            }),
            new("Communication", "Phone", "phoneDialer", "IPhoneDialer: a number in the dialer (nothing is called).", "Dial", _ =>
            {
                dialer.Open("+33600000000");
                return Task.FromResult("The dialer was asked to open.");
            }),
            new("Communication", "Browser", "browser", "IBrowser: a page in the browser of the device.", "Open", async _ =>
                await browser.OpenAsync(new Uri("https://github.com/cedric56/Routeables.SignalR"), new BrowserLaunchOptions { LaunchMode = BrowserLaunchMode.SystemPreferred })
                    ? "The page was opened." : "The page was not opened (a popup blocker?)."),
            new("Communication", "Launcher", "launcher", "ILauncher: whether a mailto: address can be opened, then opens it.", "Open mailto:", async _ =>
            {
                var uri = new Uri("mailto:someone@example.com");
                if (!await launcher.CanOpenAsync(uri))
                    return "This device cannot open mailto: addresses.";
                return await launcher.OpenAsync(uri) ? "Opened." : "Not opened.";
            }),
            new("Communication", "Map", "map", "IMap: a place on the map of the device.", "Show", async _ =>
            {
                await map.OpenAsync(48.8584, 2.2945, new MapLaunchOptions { Name = "Eiffel Tower" });
                return "The map was opened.";
            }),
            new("Communication", "Contacts", "contacts", "IContacts: a contact picked by the user.", "Pick", async _ =>
                await contacts.PickContactAsync() is { } contact ? $"Picked {contact.DisplayName}." : "No contact picked."),

            // ---------------------------------------------------------------- location
            new("Location", "Where am I", "geolocation", "IGeolocation.GetLocationAsync: the position, once (the device asks for the permission).", "Locate", async _ =>
                await geolocation.GetLocationAsync(new GeolocationRequest(GeolocationAccuracy.Medium, TimeSpan.FromSeconds(20)), CancellationToken.None) is { } location
                    ? Where(lastPlace = location) : "No position."),
            new("Location", "Last known", "geolocation", "IGeolocation.GetLastKnownLocationAsync: the last position the device knows.", "Read", async _ =>
                await geolocation.GetLastKnownLocationAsync() is { } location ? Where(location) : "None yet: locate once."),
            new("Location", "Follow", "geolocation", "IGeolocation listening: the position as it changes.", "Follow", async c =>
            {
                if (geolocation.IsListeningForeground)
                {
                    geolocation.StopListeningForeground();
                    c.Active = false;
                    return $"Stopped at {Time()}.";
                }
                c.Active = await geolocation.StartListeningForegroundAsync(new GeolocationListeningRequest(GeolocationAccuracy.Medium));
                return c.Active ? "Waiting for a position…" : "The device did not start listening.";
            }, "Stop"),
            new("Location", "Geocoding", "geocoding", "IGeocoding: the place of the last position (the Eiffel Tower without one).", "Name the place", async _ =>
            {
                var place = (await geocoding.GetPlacemarksAsync(lastPlace?.Latitude ?? 48.8584, lastPlace?.Longitude ?? 2.2945)).FirstOrDefault();
                return place is null ? "No place." : $"{place.Thoroughfare} {place.Locality} {place.CountryName}".Trim();
            }),

            // ---------------------------------------------------------------- sensors
            Sensor<AccelerometerChangedEventArgs>("Accelerometer", "accelerometer", "IAccelerometer, in g; shaking the device is detected on the server.", accelerometer.AsSensor(),
                h => accelerometer.ReadingChanged += h, h => accelerometer.ReadingChanged -= h,
                e => $"x {N(e.Reading.Acceleration.X)} · y {N(e.Reading.Acceleration.Y)} · z {N(e.Reading.Acceleration.Z)}"),
            Sensor<GyroscopeChangedEventArgs>("Gyroscope", "gyroscope", "IGyroscope, in rad/s.", gyroscope.AsSensor(),
                h => gyroscope.ReadingChanged += h, h => gyroscope.ReadingChanged -= h,
                e => $"x {N(e.Reading.AngularVelocity.X)} · y {N(e.Reading.AngularVelocity.Y)} · z {N(e.Reading.AngularVelocity.Z)}"),
            Sensor<MagnetometerChangedEventArgs>("Magnetometer", "magnetometer", "IMagnetometer, in µT.", magnetometer.AsSensor(),
                h => magnetometer.ReadingChanged += h, h => magnetometer.ReadingChanged -= h,
                e => $"x {N(e.Reading.MagneticField.X, "0.0")} · y {N(e.Reading.MagneticField.Y, "0.0")} · z {N(e.Reading.MagneticField.Z, "0.0")}"),
            Sensor<CompassChangedEventArgs>("Compass", "compass", "ICompass: the heading to the magnetic north.", compass.AsSensor(),
                h => compass.ReadingChanged += h, h => compass.ReadingChanged -= h,
                e => $"{N(e.Reading.HeadingMagneticNorth, "0")}°"),
            Sensor<BarometerChangedEventArgs>("Barometer", "barometer", "IBarometer, in hPa.", barometer.AsSensor(),
                h => barometer.ReadingChanged += h, h => barometer.ReadingChanged -= h,
                e => $"{N(e.Reading.PressureInHectopascals, "0.0")} hPa"),
            Sensor<OrientationSensorChangedEventArgs>("Orientation", "orientationSensor", "IOrientationSensor: the attitude as a quaternion.", orientation.AsSensor(),
                h => orientation.ReadingChanged += h, h => orientation.ReadingChanged -= h,
                e => $"x {N(e.Reading.Orientation.X)} · y {N(e.Reading.Orientation.Y)} · z {N(e.Reading.Orientation.Z)} · w {N(e.Reading.Orientation.W)}"),

            // ---------------------------------------------------------------- feedback and media
            new("Media", "Vibration", "vibration", "IVibration: 300 ms.", "Vibrate", _ =>
            {
                vibration.Vibrate(TimeSpan.FromMilliseconds(300));
                return Task.FromResult("Asked to vibrate.");
            }),
            new("Media", "Haptic feedback", "hapticFeedback", "IHapticFeedback: a click.", "Click", _ =>
            {
                haptic.Perform(HapticFeedbackType.Click);
                return Task.FromResult("Asked for a click.");
            }),
            new("Media", "Flashlight", "flashlight", "IFlashlight: the torch of the camera (it may ask for the camera).", "Turn on", async c =>
            {
                if (flashlightOn)
                    await flashlight.TurnOffAsync();
                else
                    await flashlight.TurnOnAsync();
                flashlightOn = !flashlightOn;
                c.Active = flashlightOn;
                return flashlightOn ? "On." : "Off.";
            }, "Turn off"),
            new("Media", "Screen reader", "semanticScreenReader", "ISemanticScreenReader: a sentence for the screen reader.", "Announce", _ =>
            {
                reader.Announce("Hello from the Routeables server.");
                return Task.FromResult("Announced (heard with a screen reader on).");
            }),
            new("Media", "Text to speech", "textToSpeech", "ITextToSpeech: a sentence said aloud.", "Speak", async _ =>
            {
                await speech.SpeakAsync("Hello from the Routeables server.");
                return "Said.";
            }),
            new("Media", "File picker", "filePicker", "IFilePicker: a file of the device, uploaded to the session.", "Pick a file", async _ =>
            {
                var file = await picker.PickAsync(new PickOptions { PickerTitle = "A file for the server" });
                if (file is null)
                    return "No file picked.";
                var size = file is RemoteFileResult remote ? remote.Size + " bytes, " : string.Empty;
                return $"{file.FileName} ({size}{file.ContentType})";
            }),
            new("Media", "Photo", "mediaPicker", "IMediaPicker: a photo of the library, uploaded to the session.", "Pick a photo", async _ =>
                await media.PickPhotoAsync() is { } photo ? $"{photo.FileName} ({photo.ContentType})" : "No photo picked."),
            new("Media", "Camera", "mediaPicker", "IMediaPicker.CapturePhotoAsync: a photo taken now (a file dialog on a desktop).", "Take a photo", async _ =>
                await media.CapturePhotoAsync() is { } photo ? $"{photo.FileName} ({photo.ContentType})" : "No photo taken."),
            new("Media", "Screenshot", "screenshot", "IScreenshot: the screen, as the device lets it be captured.", "Capture", async _ =>
            {
                var shot = await screenshot.CaptureAsync();
                return $"{shot.Width}×{shot.Height}.";
            }),
        ];

        // The state the client announced follows its changes: shown as they come.
        EssentialsCheck Find(string name) => Checks.First(c => c.Name == name);
        void OnDisplay(object? sender, DisplayInfoChangedEventArgs e) => Find("Display").Result = DisplayText(e.DisplayInfo);
        void OnBattery(object? sender, BatteryInfoChangedEventArgs e) => Find("Battery").Result = BatteryText();
        void OnNetwork(object? sender, ConnectivityChangedEventArgs e) => Find("Network").Result = NetworkText();
        void OnLocation(object? sender, GeolocationLocationChangedEventArgs e) => Find("Follow").Result = Where(lastPlace = e.Location);
        void OnShake(object? sender, EventArgs e) => Find("Accelerometer").Result = $"Shaken at {Time()}!";
        display.MainDisplayInfoChanged += OnDisplay;
        battery.BatteryInfoChanged += OnBattery;
        connectivity.ConnectivityChanged += OnNetwork;
        geolocation.LocationChanged += OnLocation;
        accelerometer.ShakeDetected += OnShake;
        _unsubscribe.Add(() =>
        {
            display.MainDisplayInfoChanged -= OnDisplay;
            battery.BatteryInfoChanged -= OnBattery;
            connectivity.ConnectivityChanged -= OnNetwork;
            geolocation.LocationChanged -= OnLocation;
            accelerometer.ShakeDetected -= OnShake;
            if (geolocation.IsListeningForeground)
                geolocation.StopListeningForeground();
            if (display.KeepScreenOn)
                display.KeepScreenOn = false;
        });

        _essentials.Ready += OnReady;
        OnReady();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The tests, by group.</summary>
    public IReadOnlyList<EssentialsCheck> Checks { get; }

    /// <summary>What the client announced, sorted.</summary>
    public IReadOnlyList<string> Capabilities => [.. _essentials.Capabilities.Order(StringComparer.Ordinal)];

    /// <summary>One line on the client: how many services it announced.</summary>
    public string Summary => !_essentials.HasAnnounced
        ? "This client announced no Essentials: every test says it is not supported."
        : $"This client announced {_essentials.Capabilities.Count} services: {Checks.Count(c => c.Supported)} of the {Checks.Count} tests can run.";

    // The client announces itself after each Hello: what it supports may change (another tab, a reconnection).
    private void OnReady()
    {
        foreach (var check in Checks)
        {
            check.Supported = check.Service.Length == 0 || _essentials.IsSupported(check.Service);
            if (!check.Supported && check.Status is "ready")
                check.Status = "unsupported";
            else if (check.Supported && check.Status is "unsupported" && check.Result.Length == 0)
                check.Status = "ready";
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Capabilities)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));
    }

    public void Dispose()
    {
        _essentials.Ready -= OnReady;
        foreach (var unsubscribe in _unsubscribe)
            unsubscribe();
    }
}

/// <summary>What the page needs of a sensor: the six MAUI sensors share these members, but no interface.</summary>
internal interface ISensorLike
{
    bool IsMonitoring { get; }

    void Start(SensorSpeed speed);

    void Stop();
}

internal static class SensorExtensions
{
    public static ISensorLike AsSensor(this IAccelerometer s) => new Like(() => s.IsMonitoring, s.Start, s.Stop);
    public static ISensorLike AsSensor(this IGyroscope s) => new Like(() => s.IsMonitoring, s.Start, s.Stop);
    public static ISensorLike AsSensor(this IMagnetometer s) => new Like(() => s.IsMonitoring, s.Start, s.Stop);
    public static ISensorLike AsSensor(this ICompass s) => new Like(() => s.IsMonitoring, s.Start, s.Stop);
    public static ISensorLike AsSensor(this IBarometer s) => new Like(() => s.IsMonitoring, s.Start, s.Stop);
    public static ISensorLike AsSensor(this IOrientationSensor s) => new Like(() => s.IsMonitoring, s.Start, s.Stop);

    private sealed class Like(Func<bool> monitoring, Action<SensorSpeed> start, Action stop) : ISensorLike
    {
        public bool IsMonitoring => monitoring();
        public void Start(SensorSpeed speed) => start(speed);
        public void Stop() => stop();
    }
}
