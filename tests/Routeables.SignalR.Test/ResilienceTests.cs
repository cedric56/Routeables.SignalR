using System.Security.Claims;
using System.Text.Json;
using Routeables.Abstraction;
using Routeables.Core;
using Routeables.SignalR;

namespace Routeables.Tests.Remote;

/// <summary>A connection that drops and comes back: the session waits for its client, and only for it.</summary>
public class ResilienceTests
{
    private static ClaimsPrincipal User(string name)
        => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, name)], "test"));

    private static ServiceProvider Services(Action<RouteablesOptions>? options = null)
    {
        var services = TestSchema.Services(options);
        services.RegisterRoute<StreamView, StreamViewModel>(route: "/stream");
        return services.BuildServiceProvider();
    }

    private static RouteablesHub Hub(ServiceProvider provider, string connection, RecordingClient client, ClaimsPrincipal? user = null)
        => new(provider, provider.GetRequiredService<RouteableSessions>())
        {
            Context = new FakeHubContext(connection, user),
            Clients = new FakeHubClients(client)
        };

    [Fact]
    public async Task A_client_that_reconnects_during_a_dialog_gets_its_page_and_its_dialog_back_and_can_answer()
    {
        await using var provider = Services();
        var sessions = provider.GetRequiredService<RouteableSessions>();
        var before = new RecordingClient();
        var first = Hub(provider, "c1", before, User("ann"));
        await first.OnConnectedAsync();
        var welcome = await first.Hello(new RouteableHello(RouteableProtocol.Version));
        Assert.True(welcome.Success);
        Assert.False(welcome.Resumed);

        await first.GoTo("/stream");
        Assert.True((await first.Invoke("count", [JsonDocument.Parse("7").RootElement])).Success);
        var ask = await first.Invoke("askAsync", []);
        Assert.True(ask.Pending);
        var dialog = Assert.Single(before.Dialogs);

        // The connection drops: the session waits.
        await first.OnDisconnectedAsync(new IOException("network"));
        Assert.Equal(1, sessions.DetachedCount);

        var after = new RecordingClient();
        var second = Hub(provider, "c2", after, User("ann"));
        await second.OnConnectedAsync();
        var resumed = await second.Hello(new RouteableHello(RouteableProtocol.Version, welcome.Session));

        Assert.True(resumed.Resumed);
        Assert.Equal(welcome.Session, resumed.Session);
        Assert.Equal(0, sessions.DetachedCount);
        Assert.Equal(1, sessions.Count);

        // The same page (same instance, a new generation), with its state, and the dialog that waits.
        var page = Assert.Single(after.Navigated);
        Assert.Equal(before.Navigated[0].Instance, page.Instance);
        Assert.True(page.Generation > before.Navigated[0].Generation);
        Assert.Equal(7, page.State["value"]);
        Assert.Equal(dialog.Id, Assert.Single(after.Dialogs).Id);

        Assert.True((await second.Answer(dialog.Id, 0)).Success);
        Assert.True(await TestSchema.EventuallyAsync(() => after.Completed.Any(c => c.Operation == ask.Operation && c.Success)));
        Assert.Contains(after.Changed, c => c.Property == "value" && Equals(c.Value, 1));

        await second.OnDisconnectedAsync(null);
    }

    [Fact]
    public async Task What_was_told_while_the_client_was_away_reaches_it_when_it_comes_back()
    {
        await using var provider = Services(o => o.CommandGrace = TimeSpan.FromMilliseconds(20));
        var before = new RecordingClient();
        var first = Hub(provider, "c1", before);
        await first.OnConnectedAsync();
        var welcome = await first.Hello(new RouteableHello(RouteableProtocol.Version));
        await first.GoTo("/stream");

        var slow = await first.Invoke("slowAsync", [JsonDocument.Parse("150").RootElement]);
        Assert.True(slow.Pending);
        await first.OnDisconnectedAsync(null);
        await Task.Delay(300, TestContext.Current.CancellationToken);   // the call ends while nobody listens

        var after = new RecordingClient();
        var second = Hub(provider, "c2", after);
        await second.OnConnectedAsync();
        Assert.True((await second.Hello(new RouteableHello(RouteableProtocol.Version, welcome.Session))).Resumed);

        Assert.Contains(after.Completed, c => c.Operation == slow.Operation && c.Success);
        Assert.Equal(1, Assert.Single(after.Navigated).State["value"]);
    }

    [Fact]
    public async Task A_session_is_resumed_only_by_its_user_and_only_within_the_grace_period()
    {
        await using var provider = Services(o => o.ReconnectGracePeriod = TimeSpan.FromMilliseconds(300));
        var sessions = provider.GetRequiredService<RouteableSessions>();
        var first = Hub(provider, "c1", new RecordingClient(), User("ann"));
        await first.OnConnectedAsync();
        var token = (await first.Hello(new RouteableHello(RouteableProtocol.Version))).Session;
        await first.OnDisconnectedAsync(null);

        var mallory = Hub(provider, "c2", new RecordingClient(), User("mallory"));
        await mallory.OnConnectedAsync();
        var stolen = await mallory.Hello(new RouteableHello(RouteableProtocol.Version, token));
        Assert.False(stolen.Resumed);
        Assert.NotEqual(token, stolen.Session);

        var anonymous = Hub(provider, "c3", new RecordingClient());
        await anonymous.OnConnectedAsync();
        Assert.False((await anonymous.Hello(new RouteableHello(RouteableProtocol.Version, token))).Resumed);

        Assert.True(await TestSchema.EventuallyAsync(() => sessions.DetachedCount == 0));
        var late = Hub(provider, "c4", new RecordingClient(), User("ann"));
        await late.OnConnectedAsync();
        Assert.False((await late.Hello(new RouteableHello(RouteableProtocol.Version, token))).Resumed);
    }

    [Fact]
    public async Task Without_a_grace_period_a_session_ends_with_its_connection()
    {
        await using var provider = Services(o => o.ReconnectGracePeriod = TimeSpan.Zero);
        var sessions = provider.GetRequiredService<RouteableSessions>();
        var hub = Hub(provider, "c1", new RecordingClient());
        await hub.OnConnectedAsync();
        await hub.GoTo("/stream");
        var session = sessions.Get("c1")!;

        await hub.OnDisconnectedAsync(null);

        Assert.True(session.IsDisposed);
        Assert.Equal(0, sessions.DetachedCount);
    }

    [Fact]
    public async Task A_client_of_another_protocol_is_told_to_update()
    {
        await using var provider = Services();
        var hub = Hub(provider, "c1", new RecordingClient());
        await hub.OnConnectedAsync();

        var welcome = await hub.Hello(new RouteableHello(1));

        Assert.False(welcome.Success);
        Assert.Equal(RouteableProtocol.Version, welcome.Protocol);
        Assert.Contains("@routeables/client", welcome.Error);
    }
}
