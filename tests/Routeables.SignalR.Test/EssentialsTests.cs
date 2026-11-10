using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Networking;
using Microsoft.Maui.Storage;
using Routeables.Abstraction.Registrations;
using Routeables.Core;
using Routeables.SignalR;
using Routeables.SignalR.Essentials;

namespace Routeables.Tests.Remote;

public sealed class LocateView;

/// <summary>A view model that asks the device of its user something and waits for the answer.</summary>
public sealed class LocateViewModel(RouteableEssentials essentials, IPreferences preferences) : INotifyPropertyChanged
{
    private string _where = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    [RouteableState] public string Where { get => _where; set { _where = value; PropertyChanged?.Invoke(this, new(nameof(Where))); } }

    [RouteableCommand]
    public async Task LocateAsync()
    {
        var answer = await essentials.InvokeAsync("geolocation", "getLocation", [], EssentialsCallOptions.Interactive);
        Where = answer.GetString() ?? string.Empty;
    }

    [RouteableCommand]
    public void Remember(string value) => preferences.Set("last", value);

    /// <summary>What the facade gives on the thread of the session, and in work it started.</summary>
    [RouteableCommand]
    public async Task ReadThroughTheFacadeAsync()
    {
        Where = Preferences.Default.Get("user", "?");
        Where += "/" + await Task.Run(() => Preferences.Default.Get("user", "?"));
    }
}

/// <summary>The device APIs of the client: calls, answers, events, reconnection, and the MAUI services built on them.</summary>
public class EssentialsTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static ClaimsPrincipal User(string name)
        => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, name)], "test"));

    private static ServiceProvider Services(Action<RouteablesOptions>? options = null)
    {
        var services = TestSchema.Services(options);
        services.AddRouteablesEssentials();
        services.RegisterRoute<LocateView, LocateViewModel>(route: "/locate");
        return services.BuildServiceProvider();
    }

    private static RouteablesHub Hub(ServiceProvider provider, string connection, IRouteableClient client, ClaimsPrincipal? user = null)
        => new(provider, provider.GetRequiredService<RouteableSessions>())
        {
            Context = new FakeHubContext(connection, user),
            Clients = new FakeHubClients(client),
        };

    private static EssentialsReady Ready(string preferences = "{}", string? connectivity = null)
    {
        var state = new Dictionary<string, JsonElement> { ["preferences"] = Json(preferences) };
        if (connectivity is not null)
            state["connectivity"] = Json(connectivity);
        return new EssentialsReady(EssentialsProtocol.Version, ["preferences", "connectivity", "geolocation"], state);
    }

    private static async Task<(RouteablesHub Hub, RecordingClient Client, RouteableSession Session)> ConnectAsync(
        ServiceProvider provider, string connection = "c1", ClaimsPrincipal? user = null, EssentialsReady? ready = null)
    {
        var client = new RecordingClient();
        var hub = Hub(provider, connection, client, user);
        await hub.OnConnectedAsync();
        Assert.True((await hub.Hello(new RouteableHello(RouteableProtocol.Version))).Success);
        if (ready is not null)
            Assert.True((await hub.EssentialsReady(ready)).Success);
        return (hub, client, provider.GetRequiredService<RouteableSessions>().Get(connection)!);
    }

    [Fact]
    public async Task A_call_reaches_the_client_and_ends_with_its_answer()
    {
        await using var provider = Services();
        var (hub, client, session) = await ConnectAsync(provider, ready: Ready());

        var call = session.Essentials.InvokeAsync("preferences", "set", ["", "k", "v"], cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(await TestSchema.EventuallyAsync(() => client.EssentialsCalls.Count == 1));
        var sent = client.EssentialsCalls[0];
        Assert.Equal(("preferences", "set"), (sent.Service, sent.Method));

        await hub.EssentialsResult(new EssentialsResult("made-up", true, Json("1")));   // nothing waits for it: dropped
        await hub.EssentialsResult(new EssentialsResult(sent.Id, true, Json("42")));

        Assert.Equal(42, (await call).GetInt32());
    }

    [Fact]
    public async Task A_failure_on_the_client_keeps_only_the_known_codes()
    {
        await using var provider = Services();
        var (hub, client, session) = await ConnectAsync(provider, ready: Ready());

        var denied = session.Essentials.InvokeAsync("geolocation", "getLocation", cancellationToken: TestContext.Current.CancellationToken);
        var odd = session.Essentials.InvokeAsync("geolocation", "getLocation", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(await TestSchema.EventuallyAsync(() => client.EssentialsCalls.Count == 2));
        await hub.EssentialsResult(new EssentialsResult(client.EssentialsCalls[0].Id, false, null, "Denied by the user", EssentialsErrorCodes.PermissionDenied));
        await hub.EssentialsResult(new EssentialsResult(client.EssentialsCalls[1].Id, false, null, null, "<script>"));

        var first = await Assert.ThrowsAsync<RouteableEssentialsException>(() => denied);
        Assert.Equal(EssentialsErrorCodes.PermissionDenied, first.Code);
        Assert.IsType<PermissionException>(first.ToMauiException());
        Assert.Equal(EssentialsErrorCodes.Failed, (await Assert.ThrowsAsync<RouteableEssentialsException>(() => odd)).Code);
    }

    [Fact]
    public async Task A_service_the_client_did_not_announce_is_not_supported()
    {
        await using var provider = Services();
        var (_, client, session) = await ConnectAsync(provider, ready: Ready());

        var failure = await Assert.ThrowsAsync<RouteableEssentialsException>(() => session.Essentials.InvokeAsync("camera", "capture",cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(EssentialsErrorCodes.NotSupported, failure.Code);
        Assert.IsType<FeatureNotSupportedException>(failure.ToMauiException());
        Assert.Empty(client.EssentialsCalls);
    }

    [Fact]
    public async Task A_call_made_before_the_client_announced_itself_waits_for_it()
    {
        await using var provider = Services();
        var (hub, client, session) = await ConnectAsync(provider);

        var call = session.Essentials.InvokeAsync("preferences", "set", ["", "k", "v"], cancellationToken: TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Empty(client.EssentialsCalls);

        await hub.EssentialsReady(Ready());
        Assert.True(await TestSchema.EventuallyAsync(() => client.EssentialsCalls.Count == 1));
        await hub.EssentialsResult(new EssentialsResult(client.EssentialsCalls[0].Id, true));
        await call;
    }

    [Fact]
    public async Task A_client_that_never_answers_ends_the_call_at_its_timeout()
    {
        await using var provider = Services(o => o.EssentialsTimeout = TimeSpan.FromMilliseconds(100));
        var (_, _, session) = await ConnectAsync(provider);

        var notReady = await Assert.ThrowsAsync<RouteableEssentialsException>(() => session.Essentials.InvokeAsync("preferences", "set",cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(EssentialsErrorCodes.NotReady, notReady.Code);
    }

    [Fact]
    public async Task A_command_that_waits_for_the_device_is_answered_as_going_on_then_completed()
    {
        await using var provider = Services();
        var (hub, client, _) = await ConnectAsync(provider, ready: Ready());
        await hub.GoTo("/locate");

        var started = System.Diagnostics.Stopwatch.StartNew();
        var ack = await hub.Invoke("locateAsync", []);
        Assert.True(ack.Pending);
        Assert.True(started.Elapsed < TimeSpan.FromMilliseconds(700), "answered as soon as the device was asked, not after the grace");

        Assert.True(await TestSchema.EventuallyAsync(() => client.EssentialsCalls.Count == 1));
        await hub.EssentialsResult(new EssentialsResult(client.EssentialsCalls[0].Id, true, Json("\"Nantes\"")));

        Assert.True(await TestSchema.EventuallyAsync(() => client.Completed.Count == 1));
        Assert.True(client.Completed[0].Success);
        Assert.Contains(client.Changed, c => c.Property == "where" && Equals(c.Value, "Nantes"));
    }

    [Fact]
    public async Task After_a_reconnection_a_write_is_sent_again_and_a_question_ends_as_lost()
    {
        await using var provider = Services();
        var (first, before, session) = await ConnectAsync(provider, user: User("ann"), ready: Ready());
        var token = session.Token;

        var write = session.Essentials.InvokeAsync("preferences", "set", ["", "k", "v"], EssentialsCallOptions.Replayable, cancellationToken: TestContext.Current.CancellationToken);
        var question = session.Essentials.InvokeAsync("geolocation", "getLocation", options: EssentialsCallOptions.Interactive, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(await TestSchema.EventuallyAsync(() => before.EssentialsCalls.Count == 2));
        await first.OnDisconnectedAsync(new IOException("network"));

        var after = new RecordingClient();
        var second = Hub(provider, "c2", after, User("ann"));
        await second.OnConnectedAsync();
        Assert.True((await second.Hello(new RouteableHello(RouteableProtocol.Version, token))).Resumed);
        await second.EssentialsReady(Ready());

        Assert.Equal(EssentialsErrorCodes.Lost, (await Assert.ThrowsAsync<RouteableEssentialsException>(() => question)).Code);
        Assert.True(await TestSchema.EventuallyAsync(() => after.EssentialsCalls.Count == 1));
        Assert.Equal(before.EssentialsCalls[0].Id, after.EssentialsCalls[0].Id);
        await second.EssentialsResult(new EssentialsResult(after.EssentialsCalls[0].Id, true));
        await write;
        await second.OnDisconnectedAsync(null);
    }

    [Fact]
    public async Task The_calls_that_wait_end_with_the_session()
    {
        await using var provider = Services(o => o.ReconnectGracePeriod = TimeSpan.Zero);
        var (hub, client, session) = await ConnectAsync(provider, ready: Ready());
        var call = session.Essentials.InvokeAsync("geolocation", "getLocation", options: EssentialsCallOptions.Interactive, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(await TestSchema.EventuallyAsync(() => client.EssentialsCalls.Count == 1));

        await hub.OnDisconnectedAsync(null);

        Assert.Equal(EssentialsErrorCodes.Cancelled, (await Assert.ThrowsAsync<RouteableEssentialsException>(() => call)).Code);
    }

    [Fact]
    public async Task Events_are_asked_once_told_on_the_session_and_asked_again_after_a_reconnection()
    {
        await using var provider = Services();
        var (first, before, session) = await ConnectAsync(provider, user: User("bob"), ready: Ready());
        var token = session.Token;
        var seen = new Told<(string? Payload, bool OnSession)>();

        using var one = session.Essentials.Subscribe("geolocation", "locationChanged", p => seen.Add((p?.GetRawText(), RouteableSession.Running == session)));
        using var two = session.Essentials.Subscribe("geolocation", "locationChanged", _ => { });
        Assert.True(await TestSchema.EventuallyAsync(() => before.EssentialsSubscriptions.Any(s => s.Event == "locationChanged")));
        Assert.Single(before.EssentialsSubscriptions, s => s.Event == "locationChanged");

        await first.EssentialsEvent(new EssentialsEvent("geolocation", "locationChanged", Json("{\"latitude\":47.2}")));
        await first.EssentialsEvent(new EssentialsEvent("geolocation", "nobodyAsked", Json("1")));
        Assert.True(await TestSchema.EventuallyAsync(() => seen.Count == 1));
        Assert.Equal(("{\"latitude\":47.2}", true), seen[0]);

        await first.OnDisconnectedAsync(new IOException("network"));
        var after = new RecordingClient();
        var second = Hub(provider, "c2", after, User("bob"));
        await second.OnConnectedAsync();
        await second.Hello(new RouteableHello(RouteableProtocol.Version, token));
        await second.EssentialsReady(Ready());

        Assert.True(await TestSchema.EventuallyAsync(() => after.EssentialsSubscriptions.Any(s => s is { Event: "locationChanged", Active: true })));
        await second.OnDisconnectedAsync(null);
    }

    [Fact]
    public async Task What_the_client_announces_is_checked()
    {
        await using var provider = Services();
        var (hub, _, _) = await ConnectAsync(provider);

        Assert.Equal(RouteableErrorCodes.Protocol, (await hub.EssentialsReady(new EssentialsReady(99, ["preferences"]))).Code);
        Assert.Equal(RouteableErrorCodes.InvalidArgument, (await hub.EssentialsReady(new EssentialsReady(EssentialsProtocol.Version, ["a b"]))).Code);
        Assert.Equal(RouteableErrorCodes.InvalidArgument, (await hub.EssentialsReady(new EssentialsReady(EssentialsProtocol.Version, [.. Enumerable.Range(0, 65).Select(i => $"s{i}")]))).Code);
    }

    // ---------------------------------------------------------------- the MAUI services

    [Fact]
    public async Task Preferences_are_read_on_the_server_and_written_on_the_client()
    {
        await using var provider = Services();
        var (hub, client, session) = await ConnectAsync(provider, ready: Ready("""{ "": { "theme": "dark", "volume": "7" }, "settings": { "on": "true" } }"""));
        var preferences = session.Services.GetRequiredService<IPreferences>();

        Assert.Equal("dark", preferences.Get("theme", "light"));
        Assert.Equal(7, preferences.Get("volume", 0));
        Assert.True(preferences.Get("on", false, "settings"));
        Assert.False(preferences.ContainsKey("on"));
        Assert.Throws<NotSupportedException>(() => preferences.Get("id", Guid.Empty));

        preferences.Set("volume", 8.5);
        Assert.Equal(8.5, preferences.Get("volume", 0d));   // at once, before the client wrote it
        Assert.True(await TestSchema.EventuallyAsync(() => client.EssentialsCalls.Count == 1));
        var write = client.EssentialsCalls[0];
        Assert.Equal("set", write.Method);
        Assert.Equal(new[] { "", "volume", "8.5" }, write.Args.Select(a => a?.ToString()).ToArray());

        await hub.EssentialsResult(new EssentialsResult(write.Id, true));
        await ((RemotePreferences)preferences).FlushAsync();
    }

    [Fact]
    public async Task A_write_the_client_had_not_acknowledged_survives_a_reconnection()
    {
        await using var provider = Services();
        var (first, _, session) = await ConnectAsync(provider, user: User("cid"), ready: Ready("""{ "": { "theme": "dark" } }"""));
        var token = session.Token;
        var preferences = session.Services.GetRequiredService<IPreferences>();

        preferences.Set("theme", "light");
        await first.OnDisconnectedAsync(new IOException("network"));

        var after = new RecordingClient();
        var second = Hub(provider, "c2", after, User("cid"));
        await second.OnConnectedAsync();
        await second.Hello(new RouteableHello(RouteableProtocol.Version, token));
        // The client announces what it has: the write never reached it, another tab added a value.
        await second.EssentialsReady(Ready("""{ "": { "theme": "dark", "lang": "fr" } }"""));

        Assert.Equal("light", preferences.Get("theme", "?"));
        Assert.Equal("fr", preferences.Get("lang", "?"));
        Assert.True(await TestSchema.EventuallyAsync(() => after.EssentialsCalls.Count == 1));
        await second.EssentialsResult(new EssentialsResult(after.EssentialsCalls[0].Id, true));
        await ((RemotePreferences)preferences).FlushAsync();
        await second.OnDisconnectedAsync(null);
    }

    [Fact]
    public async Task A_failed_write_is_reported_by_FlushAsync()
    {
        await using var provider = Services();
        var (hub, client, session) = await ConnectAsync(provider, ready: Ready());
        var preferences = (RemotePreferences)session.Services.GetRequiredService<IPreferences>();

        preferences.Set("big", new string('x', 10));
        Assert.True(await TestSchema.EventuallyAsync(() => client.EssentialsCalls.Count == 1));
        await hub.EssentialsResult(new EssentialsResult(client.EssentialsCalls[0].Id, false, null, "QuotaExceededError", EssentialsErrorCodes.Failed));

        var failure = await Assert.ThrowsAsync<AggregateException>(preferences.FlushAsync);
        Assert.IsType<RouteableEssentialsException>(Assert.Single(failure.InnerExceptions));
    }

    [Fact]
    public async Task The_facades_reach_the_user_of_the_session_that_runs_and_nobody_else()
    {
        await using var provider = Services();
        var (annHub, _, ann) = await ConnectAsync(provider, "c1", ready: Ready("""{ "": { "user": "ann" } }"""));
        var (bobHub, _, bob) = await ConnectAsync(provider, "c2", ready: Ready("""{ "": { "user": "bob" } }"""));

        Assert.Equal("ann", await ann.Dispatcher.InvokeAsync(() => Preferences.Default.Get("user", "?")));
        Assert.Equal("bob", await bob.Dispatcher.InvokeAsync(() => Preferences.Default.Get("user", "?")));
        Assert.Same(ann.Services.GetRequiredService<IPreferences>(), await ann.Dispatcher.InvokeAsync(() => RemoteFacades.Resolve<IPreferences>()));

        // In a command, and in the work it starts (Task.Run): still the user of the command.
        await annHub.GoTo("/locate");
        Assert.True((await annHub.Invoke("readThroughTheFacadeAsync", [])).Success);
        Assert.Equal("ann/ann", ((LocateViewModel)ann.Current!).Where);

        // Outside of a session: no user to ask.
        var outside = await Task.Run(() => Assert.Throws<InvalidOperationException>(() => Preferences.Default.Get("user", "?")));
        Assert.Contains("outside of a Routeables session", outside.Message);
        Assert.Null(RouteableSession.Running);
        await bobHub.OnDisconnectedAsync(null);
    }

    [Fact]
    public async Task Connectivity_is_what_the_client_reported_last()
    {
        await using var provider = Services();
        var (hub, client, session) = await ConnectAsync(provider, ready: Ready(connectivity: """{ "networkAccess": "Internet", "profiles": ["WiFi", "Nonsense"] }"""));
        var connectivity = session.Services.GetRequiredService<IConnectivity>();
        var changes = new Told<NetworkAccess>();
        connectivity.ConnectivityChanged += (_, e) => changes.Add(e.NetworkAccess);

        Assert.Equal(NetworkAccess.Internet, connectivity.NetworkAccess);
        Assert.Equal(new[] { ConnectionProfile.WiFi }, connectivity.ConnectionProfiles.ToArray());
        Assert.True(await TestSchema.EventuallyAsync(() => client.EssentialsSubscriptions.Any(s => s is { Service: "connectivity", Event: "changed", Active: true })));

        await hub.EssentialsEvent(new EssentialsEvent("connectivity", "changed", Json("""{ "networkAccess": "None", "profiles": [] }""")));

        Assert.True(await TestSchema.EventuallyAsync(() => changes.Count == 1));
        Assert.Equal(NetworkAccess.None, connectivity.NetworkAccess);
        Assert.Equal(NetworkAccess.None, changes[0]);
    }

    [Fact]
    public async Task A_client_without_preferences_says_so()
    {
        await using var provider = Services();
        var (_, _, session) = await ConnectAsync(provider, ready: new EssentialsReady(EssentialsProtocol.Version, ["connectivity"]));
        var preferences = session.Services.GetRequiredService<IPreferences>();

        Assert.Throws<FeatureNotSupportedException>(() => preferences.Get("theme", "light"));
        Assert.Throws<FeatureNotSupportedException>(() => preferences.Set("theme", "light"));
    }
}
