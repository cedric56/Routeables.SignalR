using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
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
using Routeables.SignalR;
using Routeables.SignalR.Essentials;

namespace Routeables.Tests.Remote;

/// <summary>A client driven by the test: it announces services, sees the calls of the server and answers them.</summary>
internal sealed class EssentialsDevice
{
    private readonly HashSet<string> _answered = [];

    public required ServiceProvider Provider { get; init; }
    public required RouteablesHub Hub { get; init; }
    public required RecordingClient Client { get; init; }
    public required RouteableSession Session { get; init; }

    public T Get<T>() where T : notnull => Session.Services.GetRequiredService<T>();

    public static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public static EssentialsReady Ready(params (string Service, string State)[] services)
        => new(EssentialsProtocol.Version, [.. services.Select(s => s.Service)],
            services.Where(s => s.State.Length > 0).ToDictionary(s => s.Service, s => Json(s.State)));

    public static async Task<EssentialsDevice> ConnectAsync(EssentialsReady ready, string connection = "c1", ClaimsPrincipal? user = null, ServiceProvider? provider = null)
    {
        if (provider is null)
        {
            var services = TestSchema.Services();
            services.AddRouteablesEssentials(o => o.MaxFileSize = 1024);
            provider = services.BuildServiceProvider();
        }
        var client = new RecordingClient();
        var hub = new RouteablesHub(provider, provider.GetRequiredService<RouteableSessions>())
        {
            Context = new FakeHubContext(connection, user),
            Clients = new FakeHubClients(client),
        };
        await hub.OnConnectedAsync();
        await hub.Hello(new RouteableHello(RouteableProtocol.Version));
        Assert.True((await hub.EssentialsReady(ready)).Success);
        return new EssentialsDevice
        {
            Provider = provider, Hub = hub, Client = client,
            Session = provider.GetRequiredService<RouteableSessions>().Get(connection)!,
        };
    }

    /// <summary>The next call of <paramref name="service"/>.<paramref name="method"/> not answered yet.</summary>
    public async Task<EssentialsCall> CallAsync(string service, string method)
    {
        EssentialsCall? found = null;
        Assert.True(await TestSchema.EventuallyAsync(() =>
            (found = Client.EssentialsCalls.FirstOrDefault(c => c.Service == service && c.Method == method && !_answered.Contains(c.Id))) is not null),
            $"no call {service}.{method}");
        _answered.Add(found!.Id);
        return found;
    }

    public async Task<EssentialsCall> AnswerAsync(string service, string method, string result = "null")
    {
        var call = await CallAsync(service, method);
        await Hub.EssentialsResult(new EssentialsResult(call.Id, true, Json(result)));
        return call;
    }

    public async Task FailAsync(string service, string method, string code)
    {
        var call = await CallAsync(service, method);
        await Hub.EssentialsResult(new EssentialsResult(call.Id, false, null, "refused", code));
    }

    public Task OnSession(Action action) => Session.Dispatcher.InvokeAsync(action);

    public Task<T> OnSession<T>(Func<T> work) => Session.Dispatcher.InvokeAsync(work);

    public static JsonElement Arg(EssentialsCall call, int index = 0) => JsonSerializer.SerializeToElement(call.Args[index], JsonSerializerOptions.Web);
}

/// <summary>Every MAUI Essentials service of Routeables.SignalR.Essentials, against the real MAUI contracts.</summary>
public class EssentialsServicesTests
{
    private static Task<EssentialsDevice> Device(params (string, string)[] services) => EssentialsDevice.ConnectAsync(EssentialsDevice.Ready(services));

    private static readonly Type[] Contracts =
    [
        typeof(IAccelerometer), typeof(IAppActions), typeof(IAppInfo), typeof(IBarometer), typeof(IBattery), typeof(IBrowser), typeof(IClipboard),
        typeof(ICompass), typeof(IConnectivity), typeof(IContacts), typeof(IDeviceDisplay), typeof(IDeviceInfo), typeof(IEmail), typeof(IFilePicker),
        typeof(IFileSystem), typeof(IFlashlight), typeof(IGeocoding), typeof(IGeolocation), typeof(IGyroscope), typeof(IHapticFeedback),
        typeof(ILauncher), typeof(IMagnetometer), typeof(IMap), typeof(IMediaPicker), typeof(IOrientationSensor), typeof(IPhoneDialer),
        typeof(IPreferences), typeof(IScreenshot), typeof(ISecureStorage), typeof(ISemanticScreenReader), typeof(IShare), typeof(ISms),
        typeof(ITextToSpeech), typeof(IVersionTracking), typeof(IVibration), typeof(IWebAuthenticator),
    ];

    [Fact]
    public async Task Every_contract_is_registered_per_session_and_reached_by_its_facade()
    {
        var device = await Device(
            ("appInfo", """{ "packageName": "org.zoo", "name": "Zoo", "versionString": "2.1", "buildString": "21", "requestedTheme": "Dark" }"""),
            ("deviceInfo", """{ "model": "Pixel", "manufacturer": "Google", "platform": "Android", "idiom": "Phone", "deviceType": "Physical", "versionString": "14" }"""),
            ("deviceDisplay", """{ "display": { "width": 1080, "height": 2400, "density": 2.75, "orientation": "Portrait", "rotation": "Rotation0" } }"""),
            ("battery", """{ "chargeLevel": 0.42, "state": "Discharging", "powerSource": "Battery", "energySaverStatus": "Off" }"""),
            ("vibration", """{ "isSupported": true }"""), ("accelerometer", """{ "isSupported": true }"""), ("preferences", "{}"));
        await using var _ = device.Provider;
        var other = await EssentialsDevice.ConnectAsync(EssentialsDevice.Ready(("appInfo", """{ "name": "Other" }""")), "c2", provider: device.Provider);

        foreach (var contract in Contracts)
        {
            Assert.NotNull(device.Session.Services.GetRequiredService(contract));
            Assert.NotSame(device.Session.Services.GetRequiredService(contract), other.Session.Services.GetRequiredService(contract));
        }

        var seen = await device.OnSession(() => (
            AppInfo.Current.Name, AppInfo.Current.Version, AppInfo.Current.RequestedTheme,
            DeviceInfo.Current.Model, DeviceInfo.Current.Platform, DeviceInfo.Current.Idiom,
            DeviceDisplay.Current.MainDisplayInfo.Width, Battery.Default.ChargeLevel, Battery.Default.State,
            Vibration.Default.IsSupported, Accelerometer.Default.IsSupported, Gyroscope.Default.IsSupported, FileSystem.Current.CacheDirectory));
        Assert.Equal(("Zoo", new Version(2, 1), AppTheme.Dark), (seen.Item1, seen.Item2, seen.Item3));
        Assert.Equal(("Pixel", DevicePlatform.Android, DeviceIdiom.Phone), (seen.Item4, seen.Item5, seen.Item6));
        Assert.Equal((1080d, 0.42, BatteryState.Discharging), (seen.Item7, seen.Item8, seen.Item9));
        Assert.Equal((true, true, false), (seen.Item10, seen.Item11, seen.Item12));
        Assert.Equal("Other", await other.OnSession(() => AppInfo.Current.Name));
        Assert.NotEqual(seen.Item13, await other.OnSession(() => FileSystem.Current.CacheDirectory));
    }

    [Fact]
    public async Task The_display_tells_its_changes_and_keeps_the_screen_on_across_a_reconnection()
    {
        var device = await Device(("deviceDisplay", """{ "keepScreenOn": false, "display": { "width": 800, "height": 600, "density": 1 } }"""));
        await using var _ = device.Provider;
        var display = device.Get<IDeviceDisplay>();
        var changes = new Told<double>();
        display.MainDisplayInfoChanged += (_, e) => changes.Add(e.DisplayInfo.Width);

        display.KeepScreenOn = true;
        Assert.True(EssentialsDevice.Arg(await device.AnswerAsync("deviceDisplay", "setKeepScreenOn")).GetBoolean());
        await device.Hub.EssentialsEvent(new EssentialsEvent("deviceDisplay", "changed", EssentialsDevice.Json("""{ "width": 600, "height": 800, "density": 1, "orientation": "Portrait" }""")));
        Assert.True(await TestSchema.EventuallyAsync(() => changes.Count == 1));
        Assert.Equal(DisplayOrientation.Portrait, display.MainDisplayInfo.Orientation);

        // The client announces itself again without the wake lock (it reconnected): it is asked again.
        await device.Hub.EssentialsReady(EssentialsDevice.Ready(("deviceDisplay", """{ "keepScreenOn": false, "display": { "width": 600, "height": 800 } }""")));
        await device.AnswerAsync("deviceDisplay", "setKeepScreenOn");
    }

    [Fact]
    public async Task The_battery_tells_what_changed()
    {
        var device = await Device(("battery", """{ "chargeLevel": 0.5, "state": "Discharging", "energySaverStatus": "Off" }"""));
        await using var _ = device.Provider;
        var battery = device.Get<IBattery>();
        var levels = new Told<double>();
        var savers = new Told<EnergySaverStatus>();
        battery.BatteryInfoChanged += (_, e) => levels.Add(e.ChargeLevel);
        battery.EnergySaverStatusChanged += (_, e) => savers.Add(e.EnergySaverStatus);

        await device.Hub.EssentialsEvent(new EssentialsEvent("battery", "changed", EssentialsDevice.Json("""{ "chargeLevel": 0.49, "state": "Discharging", "energySaverStatus": "On" }""")));
        await device.Hub.EssentialsEvent(new EssentialsEvent("battery", "changed", EssentialsDevice.Json("""{ "chargeLevel": 7, "state": "Charging", "energySaverStatus": "On" }""")));

        Assert.True(await TestSchema.EventuallyAsync(() => levels.Count == 2));
        Assert.Equal(new[] { 0.49, 1 }, levels.ToArray());   // clamped to 0..1
        Assert.Equal(EnergySaverStatus.On, Assert.Single(savers));
        Assert.Equal(BatteryState.Charging, battery.State);
    }

    [Fact]
    public async Task Synchronous_commands_are_sent_and_refused_when_the_device_lacks_them()
    {
        var device = await Device(("vibration", """{ "isSupported": true }"""), ("hapticFeedback", """{ "isSupported": false }"""),
            ("phoneDialer", """{ "isSupported": true }"""), ("semanticScreenReader", ""));
        await using var _ = device.Provider;

        device.Get<IVibration>().Vibrate();
        Assert.Equal(500, EssentialsDevice.Arg(await device.AnswerAsync("vibration", "vibrate")).GetDouble());
        device.Get<IVibration>().Vibrate(TimeSpan.FromMinutes(1));
        Assert.Equal(5000, EssentialsDevice.Arg(await device.AnswerAsync("vibration", "vibrate")).GetDouble());
        device.Get<IPhoneDialer>().Open(" +33 1 23 45 67 89 ");
        Assert.Equal("+33 1 23 45 67 89", EssentialsDevice.Arg(await device.AnswerAsync("phoneDialer", "open")).GetString());
        device.Get<ISemanticScreenReader>().Announce("Saved");
        await device.AnswerAsync("semanticScreenReader", "announce");

        Assert.Throws<FeatureNotSupportedException>(() => device.Get<IHapticFeedback>().Perform(HapticFeedbackType.Click));
        Assert.Throws<FeatureNotSupportedException>(() => device.Get<IFlashlight>().TurnOnAsync().GetAwaiter().GetResult());
        Assert.False(await device.Get<IFlashlight>().IsSupportedAsync());
    }

    [Fact]
    public async Task A_location_comes_from_the_device_and_a_refusal_is_a_PermissionException()
    {
        var device = await Device(("geolocation", """{ "isEnabled": true }"""));
        await using var _ = device.Provider;
        var geolocation = device.Get<IGeolocation>();

        var asked = geolocation.GetLocationAsync(new GeolocationRequest(GeolocationAccuracy.High, TimeSpan.FromSeconds(5)), CancellationToken.None);
        var call = await device.AnswerAsync("geolocation", "getLocation", """{ "latitude": 47.218, "longitude": -1.553, "accuracy": 12, "timestamp": 1762700000000 }""");
        var location = await asked;
        Assert.Equal((47.218, -1.553, 12d), (location!.Latitude, location.Longitude, location.Accuracy!.Value));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1762700000000), location.Timestamp);
        Assert.Equal(("High", 5000d), (EssentialsDevice.Arg(call).GetProperty("desiredAccuracy").GetString(), EssentialsDevice.Arg(call).GetProperty("timeout").GetDouble()));

        var refused = geolocation.GetLocationAsync(new GeolocationRequest(), CancellationToken.None);
        await device.FailAsync("geolocation", "getLocation", EssentialsErrorCodes.PermissionDenied);
        await Assert.ThrowsAsync<PermissionException>(() => refused);
        Assert.False(geolocation.IsEnabled);

        var timedOut = geolocation.GetLocationAsync(new GeolocationRequest(), CancellationToken.None);
        await device.FailAsync("geolocation", "getLocation", EssentialsErrorCodes.Timeout);
        Assert.Null(await timedOut);

        var nonsense = geolocation.GetLocationAsync(new GeolocationRequest(), CancellationToken.None);
        await device.AnswerAsync("geolocation", "getLocation", """{ "latitude": 200, "longitude": 0 }""");
        Assert.Null(await nonsense);
    }

    [Fact]
    public async Task A_listener_gets_the_locations_and_is_started_again_after_a_reconnection()
    {
        var device = await Device(("geolocation", """{ "isEnabled": true }"""));
        await using var _ = device.Provider;
        var geolocation = device.Get<IGeolocation>();
        var locations = new Told<double>();
        geolocation.LocationChanged += (_, e) => locations.Add(e.Location.Latitude);

        var starting = geolocation.StartListeningForegroundAsync(new GeolocationListeningRequest(GeolocationAccuracy.Best, TimeSpan.FromSeconds(2)));
        Assert.Equal(2000, EssentialsDevice.Arg(await device.AnswerAsync("geolocation", "startListening", "true")).GetProperty("minimumTime").GetDouble());
        Assert.True(await starting);
        Assert.True(geolocation.IsListeningForeground);
        await Assert.ThrowsAsync<InvalidOperationException>(() => geolocation.StartListeningForegroundAsync(new GeolocationListeningRequest()));

        await device.Hub.EssentialsEvent(new EssentialsEvent("geolocation", "locationChanged", EssentialsDevice.Json("""{ "latitude": 1, "longitude": 2 }""")));
        Assert.True(await TestSchema.EventuallyAsync(() => locations.Count == 1));

        await device.Hub.EssentialsReady(EssentialsDevice.Ready(("geolocation", """{ "isEnabled": true }""")));
        await device.AnswerAsync("geolocation", "startListening", "true");

        geolocation.StopListeningForeground();
        await device.AnswerAsync("geolocation", "stopListening");
        Assert.False(geolocation.IsListeningForeground);
        await device.Hub.EssentialsEvent(new EssentialsEvent("geolocation", "locationChanged", EssentialsDevice.Json("""{ "latitude": 3, "longitude": 4 }""")));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Single(locations);
    }

    [Fact]
    public async Task A_sensor_starts_reads_detects_a_shake_and_stops()
    {
        var device = await Device(("accelerometer", """{ "isSupported": true }"""), ("compass", """{ "isSupported": false }"""));
        await using var _ = device.Provider;
        var accelerometer = device.Get<IAccelerometer>();
        var readings = new Told<float>();
        var shakes = new Told<bool>();
        accelerometer.ReadingChanged += (_, e) => readings.Add(e.Reading.Acceleration.X);
        accelerometer.ShakeDetected += (_, _) => shakes.Add(true);

        accelerometer.Start(SensorSpeed.Game);
        Assert.Equal("Game", EssentialsDevice.Arg(await device.AnswerAsync("accelerometer", "start")).GetProperty("speed").GetString());
        Assert.Throws<InvalidOperationException>(() => accelerometer.Start(SensorSpeed.UI));
        for (var i = 0; i < 8; i++)
        {
            await device.Hub.EssentialsEvent(new EssentialsEvent("accelerometer", "reading", EssentialsDevice.Json("""{ "x": 2, "y": 0, "z": 0 }""")));
            await Task.Delay(45, TestContext.Current.CancellationToken);
        }
        Assert.True(await TestSchema.EventuallyAsync(() => readings.Count == 8));
        Assert.NotEmpty(shakes);

        accelerometer.Stop();
        await device.AnswerAsync("accelerometer", "stop");
        Assert.False(accelerometer.IsMonitoring);
        Assert.Throws<FeatureNotSupportedException>(() => device.Get<ICompass>().Start(SensorSpeed.Default));
    }

    [Fact]
    public async Task Text_to_speech_gives_MAUI_locales_and_cancels_the_speech()
    {
        var device = await Device(("textToSpeech", ""));
        await using var _ = device.Provider;
        var speech = device.Get<ITextToSpeech>();

        var locales = speech.GetLocalesAsync();
        await device.AnswerAsync("textToSpeech", "getLocales", """[{ "language": "fr", "country": "FR", "name": "Amélie", "id": "fr-FR-1" }]""");
        var locale = Assert.Single(await locales);
        Assert.Equal(("fr", "FR", "Amélie", "fr-FR-1"), (locale.Language, locale.Country, locale.Name, locale.Id));

        using var cancel = new CancellationTokenSource();
        var speaking = speech.SpeakAsync("Bonjour", new SpeechOptions { Locale = locale, Volume = 0.5f }, cancel.Token);
        var call = await device.CallAsync("textToSpeech", "speak");
        Assert.Equal("fr-FR-1", EssentialsDevice.Arg(call).GetProperty("locale").GetProperty("id").GetString());
        cancel.Cancel();
        await Assert.ThrowsAsync<TaskCanceledException>(() => speaking);
        await device.AnswerAsync("textToSpeech", "cancel");
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => speech.SpeakAsync("x", new SpeechOptions { Volume = 3 }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Secure_storage_knows_its_keys_not_its_values()
    {
        var device = await Device(("secureStorage", """{ "keys": ["token"] }"""));
        await using var _ = device.Provider;
        var storage = (RemoteSecureStorage)device.Get<ISecureStorage>();

        var read = storage.GetAsync("token");
        await device.AnswerAsync("secureStorage", "get", "\"s3cr3t\"");
        Assert.Equal("s3cr3t", await read);

        Assert.True(storage.Remove("token"));
        Assert.False(storage.Remove("token"));
        await device.AnswerAsync("secureStorage", "remove");
        await device.FailAsync("secureStorage", "remove", EssentialsErrorCodes.Failed);
        await Assert.ThrowsAsync<AggregateException>(storage.FlushAsync);

        var write = storage.SetAsync("token", "new");
        await device.AnswerAsync("secureStorage", "set");
        await write;
    }

    [Fact]
    public async Task The_web_authenticator_only_accepts_the_callback_it_asked_for()
    {
        var device = await Device(("webAuthenticator", ""));
        await using var _ = device.Provider;
        var authenticator = device.Get<IWebAuthenticator>();
        var options = new WebAuthenticatorOptions { Url = new Uri("https://idp.example/authorize"), CallbackUrl = new Uri("https://zoo.example/auth/done") };

        var good = authenticator.AuthenticateAsync(options, TestContext.Current.CancellationToken);
        await device.AnswerAsync("webAuthenticator", "authenticate", "\"https://zoo.example/auth/done#access_token=abc&state=1\"");
        Assert.Equal("abc", (await good).AccessToken);

        var forged = authenticator.AuthenticateAsync(options, TestContext.Current.CancellationToken);
        await device.AnswerAsync("webAuthenticator", "authenticate", "\"https://evil.example/auth/done#access_token=abc\"");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => forged);

        var cancelled = authenticator.AuthenticateAsync(options, TestContext.Current.CancellationToken);
        await device.AnswerAsync("webAuthenticator", "authenticate", "null");
        await Assert.ThrowsAsync<TaskCanceledException>(() => cancelled);
    }

    [Fact]
    public async Task The_launcher_never_asks_the_client_to_run_code()
    {
        var device = await Device(("launcher", ""), ("browser", ""));
        await using var _ = device.Provider;
        var launcher = device.Get<ILauncher>();

        Assert.False(await launcher.OpenAsync(new Uri("javascript:alert(document.cookie)")));
        Assert.False(await launcher.CanOpenAsync(new Uri("data:text/html,hi")));
        Assert.False(await launcher.TryOpenAsync(new Uri("file:///etc/passwd")));
        await Assert.ThrowsAsync<ArgumentException>(() => device.Get<IBrowser>().OpenAsync(new Uri("javascript:x"), new BrowserLaunchOptions()));
        Assert.DoesNotContain(device.Client.EssentialsCalls, c => c.Service == "launcher");

        var open = launcher.OpenAsync(new Uri("mailto:zoo@example.org"));
        await device.AnswerAsync("launcher", "open", "true");
        Assert.True(await open);
    }

    [Fact]
    public async Task Communication_calls_carry_what_the_client_needs()
    {
        var device = await Device(("sms", """{ "isComposeSupported": true }"""), ("email", """{ "isComposeSupported": false }"""), ("map", ""),
            ("contacts", ""), ("geocoding", ""), ("clipboard", """{ "hasText": false }"""), ("share", ""));
        await using var _ = device.Provider;

        var sms = device.Get<ISms>().ComposeAsync(new SmsMessage("Hello", ["+331", "+332"]));
        Assert.Equal(2, EssentialsDevice.Arg(await device.AnswerAsync("sms", "compose")).GetProperty("recipients").GetArrayLength());
        await sms;
        await Assert.ThrowsAsync<FeatureNotSupportedException>(() => device.Get<IEmail>().ComposeAsync(new EmailMessage()));

        var map = device.Get<IMap>().OpenAsync(47.2, -1.5, new MapLaunchOptions { Name = "Zoo", NavigationMode = NavigationMode.Walking });
        Assert.Equal("Walking", EssentialsDevice.Arg(await device.AnswerAsync("map", "open")).GetProperty("navigationMode").GetString());
        await map;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => device.Get<IMap>().OpenAsync(91, 0, new MapLaunchOptions()));

        var picked = device.Get<IContacts>().PickContactAsync();
        await device.AnswerAsync("contacts", "select", """[{ "givenName": "Ann", "familyName": "Lee", "phones": ["+33"], "emails": ["ann@example.org"] }]""");
        var contact = await picked;
        Assert.Equal(("Ann", "+33", "ann@example.org"), (contact!.GivenName, contact.Phones[0].PhoneNumber, contact.Emails[0].EmailAddress));

        var placemarks = device.Get<IGeocoding>().GetPlacemarksAsync(47.2, -1.5);
        await device.AnswerAsync("geocoding", "getPlacemarks", """[{ "locality": "Nantes", "countryCode": "FR" }]""");
        Assert.Equal("Nantes", Assert.Single(await placemarks).Locality);

        var clipboard = device.Get<IClipboard>();
        var changed = new Told<bool>();
        clipboard.ClipboardContentChanged += (_, _) => changed.Add(true);
        var set = clipboard.SetTextAsync("copied");
        await device.AnswerAsync("clipboard", "setText");
        await set;
        Assert.True(clipboard.HasText);
        Assert.Single(changed);

        var share = device.Get<IShare>().RequestAsync(new ShareTextRequest("Look", "Zoo"));
        Assert.Equal("Look", EssentialsDevice.Arg(await device.AnswerAsync("share", "shareText")).GetProperty("text").GetString());
        await share;
    }

    [Fact]
    public async Task App_actions_are_not_supported_by_a_browser()
    {
        var device = await Device(("appActions", """{ "isSupported": false }"""));
        await using var _ = device.Provider;

        Assert.False(device.Get<IAppActions>().IsSupported);
        Assert.Throws<FeatureNotSupportedException>(() => device.Get<IAppActions>().GetAsync().GetAwaiter().GetResult());
    }

    [Fact]
    public async Task Version_tracking_reads_and_writes_the_preferences_of_the_client()
    {
        var device = await Device(("appInfo", """{ "packageName": "org.zoo", "versionString": "2.0", "buildString": "20" }"""),
            ("preferences", """{ "org.zoo.microsoft.maui.essentials.versiontracking": { "VersionTracking.Versions": "1.0|1.5", "VersionTracking.Builds": "10|15" } }"""));
        await using var _ = device.Provider;
        var tracking = device.Get<IVersionTracking>();

        Assert.False(tracking.IsFirstLaunchEver);
        Assert.True(tracking.IsFirstLaunchForCurrentVersion);
        Assert.Equal(("1.5", "1.0", "15"), (tracking.PreviousVersion, tracking.FirstInstalledVersion, tracking.PreviousBuild));
        Assert.Equal(new[] { "1.0", "1.5", "2.0" }, tracking.VersionHistory.ToArray());
        var write = EssentialsDevice.Arg(await device.AnswerAsync("preferences", "set"), 2);
        Assert.Equal("1.0|1.5|2.0", write.GetString());
    }

    // ---------------------------------------------------------------- files

    private static async Task<(int Status, string Body)> UploadAsync(EssentialsDevice device, string url, byte[] content, string name, string type = "image/png", ClaimsPrincipal? user = null)
    {
        var context = new DefaultHttpContext { RequestServices = device.Provider };
        if (user is not null)
            context.User = user;
        context.Request.Method = "POST";
        context.Request.Body = new MemoryStream(content);
        context.Request.ContentLength = content.Length;
        context.Request.ContentType = type;
        context.Request.Headers["X-File-Name"] = Uri.EscapeDataString(name);
        context.Response.Body = new MemoryStream();
        await RemoteFileLinks.UploadAsync(context, url[(url.LastIndexOf('/') + 1)..]);
        context.Response.Body.Position = 0;
        return (context.Response.StatusCode, await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    private static async Task<DefaultHttpContext> DownloadAsync(EssentialsDevice device, string url, ClaimsPrincipal? user = null)
    {
        var context = new DefaultHttpContext { RequestServices = device.Provider };
        if (user is not null)
            context.User = user;
        context.Response.Body = new MemoryStream();
        await RemoteFileLinks.DownloadAsync(context, url[(url.LastIndexOf('/') + 1)..]);
        context.Response.Body.Position = 0;
        return context;
    }

    [Fact]
    public async Task A_picked_file_is_uploaded_to_the_session_and_only_while_the_pick_waits()
    {
        var device = await Device(("filePicker", ""));
        await using var _ = device.Provider;
        var picker = device.Get<IFilePicker>();

        var picking = picker.PickAsync(new PickOptions
        {
            PickerTitle = "Photo",
            FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>> { [DevicePlatform.WinUI] = [".png", "*.jpg"] }),
        });
        var call = await device.CallAsync("filePicker", "pick");
        var request = EssentialsDevice.Arg(call);
        Assert.Equal(".png,.jpg", request.GetProperty("accept").GetString());
        var upload = request.GetProperty("upload").GetString()!;
        Assert.StartsWith("/routeables/essentials/files/", upload);

        var (status, body) = await UploadAsync(device, upload, Encoding.UTF8.GetBytes("PNG!"), "../../etc/cat.png");
        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal(StatusCodes.Status409Conflict, (await UploadAsync(device, upload, [1], "second.png")).Status);   // one file for PickAsync
        Assert.Equal(StatusCodes.Status404NotFound, (await UploadAsync(device, upload + "x", [1], "a.png")).Status);
        var id = JsonDocument.Parse(body).RootElement.GetProperty("file").GetString()!;
        await device.Hub.EssentialsResult(new EssentialsResult(call.Id, true, JsonSerializer.SerializeToElement(new[] { id, "made-up" })));

        var file = Assert.IsType<RemoteFileResult>(await picking);
        Assert.Equal(("cat.png", "image/png", 4L), (file.FileName, file.ContentType, file.Size));
        Assert.Equal("PNG!", await File.ReadAllTextAsync(file.FullPath, TestContext.Current.CancellationToken));
        Assert.Equal(StatusCodes.Status404NotFound, (await UploadAsync(device, upload, [1], "late.png")).Status);   // the pick ended

        await device.Session.DisposeAsync();
        Assert.False(File.Exists(file.FullPath));   // deleted with the session
    }

    [Fact]
    public async Task An_upload_larger_than_the_limit_is_refused()
    {
        var device = await Device(("mediaPicker", """{ "isCaptureSupported": true }"""));
        await using var _ = device.Provider;

        var capturing = device.Get<IMediaPicker>().CapturePhotoAsync();
        var call = await device.CallAsync("mediaPicker", "capture");
        Assert.Equal("photo", EssentialsDevice.Arg(call).GetProperty("kind").GetString());
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, (await UploadAsync(device, EssentialsDevice.Arg(call).GetProperty("upload").GetString()!, new byte[2048], "big.png")).Status);
        await device.Hub.EssentialsResult(new EssentialsResult(call.Id, true, EssentialsDevice.Json("null")));
        Assert.Null(await capturing);
    }

    [Fact]
    public async Task A_file_shared_by_a_view_model_is_downloaded_safely_by_its_user_only()
    {
        var ann = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "ann")], "test"));
        var bob = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "bob")], "test"));
        var device = await EssentialsDevice.ConnectAsync(EssentialsDevice.Ready(("share", "")), user: ann);
        await using var _ = device.Provider;
        var path = Path.Combine(device.Get<IFileSystem>().CacheDirectory, "page.html");
        await File.WriteAllTextAsync(path, "<script>alert(1)</script>", TestContext.Current.CancellationToken);

        var sharing = device.Get<IShare>().RequestAsync(new ShareFileRequest("Page", new ShareFile(path, "text/html")));
        var file = EssentialsDevice.Arg(await device.AnswerAsync("share", "shareFiles")).GetProperty("files")[0];
        await sharing;
        Assert.Equal(("page.html", "text/html"), (file.GetProperty("name").GetString(), file.GetProperty("contentType").GetString()));
        var url = file.GetProperty("url").GetString()!;

        var download = await DownloadAsync(device, url, ann);
        Assert.Equal(StatusCodes.Status200OK, download.Response.StatusCode);
        Assert.Equal("application/octet-stream", download.Response.ContentType);   // never rendered on the origin of the app
        Assert.Equal("nosniff", download.Response.Headers.XContentTypeOptions.ToString());
        Assert.StartsWith("sandbox", download.Response.Headers.ContentSecurityPolicy.ToString());
        Assert.StartsWith("attachment", download.Response.Headers.ContentDisposition.ToString());
        Assert.Equal("<script>alert(1)</script>", await new StreamReader(download.Response.Body).ReadToEndAsync(TestContext.Current.CancellationToken));

        Assert.Equal(StatusCodes.Status404NotFound, (await DownloadAsync(device, url, bob)).Response.StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, (await DownloadAsync(device, url)).Response.StatusCode);
    }

    [Fact]
    public async Task A_screenshot_is_a_png_of_the_session()
    {
        var device = await Device(("screenshot", """{ "isCaptureSupported": true }"""));
        await using var _ = device.Provider;

        var capturing = device.Get<IScreenshot>().CaptureAsync();
        var call = await device.CallAsync("screenshot", "capture");
        var (_, body) = await UploadAsync(device, EssentialsDevice.Arg(call).GetProperty("upload").GetString()!, [137, 80, 78, 71], "screen.png");
        var id = JsonDocument.Parse(body).RootElement.GetProperty("file").GetString();
        await device.Hub.EssentialsResult(new EssentialsResult(call.Id, true, JsonSerializer.SerializeToElement(new { file = id, width = 1280, height = 720 })));

        var screenshot = await capturing;
        Assert.Equal((1280, 720), (screenshot.Width, screenshot.Height));
        using var copy = new MemoryStream();
        await screenshot.CopyToAsync(copy);
        Assert.Equal(4, copy.Length);
        await Assert.ThrowsAsync<NotSupportedException>(() => screenshot.OpenReadAsync(ScreenshotFormat.Jpeg));
    }

    [Fact]
    public async Task The_file_system_of_a_session_is_its_own()
    {
        var device = await Device();
        await using var _ = device.Provider;
        var fileSystem = device.Get<IFileSystem>();

        Assert.True(Directory.Exists(fileSystem.AppDataDirectory));
        Assert.False(await fileSystem.AppPackageFileExistsAsync("../appsettings.json"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => fileSystem.OpenAppPackageFileAsync("index.html"));
    }
}
