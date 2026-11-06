using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Routeables.Abstraction;
using Routeables.Core;
using Routeables.Routing;
using Routeables.SignalR;

namespace Routeables.Tests.Remote;

internal sealed class FakeHubContext(string id) : HubCallerContext
{
    private readonly CancellationTokenSource _aborted = new();

    public override string ConnectionId => id;
    public override string? UserIdentifier => null;
    public override ClaimsPrincipal? User => null;
    public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
    public override IFeatureCollection Features { get; } = new FeatureCollection();
    public override CancellationToken ConnectionAborted => _aborted.Token;
    public override void Abort() => _aborted.Cancel();
}

/// <summary>What a hub sees as its clients: every one is the caller.</summary>
internal sealed class FakeHubClients(IRouteableClient caller) : IHubCallerClients<IRouteableClient>
{
    public IRouteableClient Caller => caller;
    public IRouteableClient Others => caller;
    public IRouteableClient All => caller;
    public IRouteableClient OthersInGroup(string groupName) => caller;
    public IRouteableClient AllExcept(IReadOnlyList<string> excludedConnectionIds) => caller;
    public IRouteableClient Client(string connectionId) => caller;
    public IRouteableClient Clients(IReadOnlyList<string> connectionIds) => caller;
    public IRouteableClient Group(string groupName) => caller;
    public IRouteableClient GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => caller;
    public IRouteableClient Groups(IReadOnlyList<string> groupNames) => caller;
    public IRouteableClient User(string userId) => caller;
    public IRouteableClient Users(IReadOnlyList<string> userIds) => caller;
}

/// <summary>The hub a browser talks to: one session per connection, and the methods that reach it.</summary>
public class RouteablesHubTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRouteablesSignalR();
        services.RegisterRoute<ShopView, ShopViewModel>(route: "/shop");
        services.AddRegion("side");
        services.AddRouteableTarget<MenuViewModel>("$menu");
        return services.BuildServiceProvider();
    }

    private static RouteablesHub Connect(ServiceProvider provider, RouteableSessions sessions, string id, RecordingClient client)
        => new(provider, sessions)
        {
            Context = new FakeHubContext(id),
            Clients = new FakeHubClients(client)
        };

    [Fact]
    public async Task A_connection_has_a_session_until_it_closes()
    {
        await using var provider = Services();
        var sessions = provider.GetRequiredService<RouteableSessions>();
        var hub = Connect(provider, sessions, "c1", new RecordingClient());

        await hub.OnConnectedAsync();
        Assert.Equal(1, sessions.Count);

        await hub.OnDisconnectedAsync(null);
        Assert.Equal(0, sessions.Count);
    }

    [Fact]
    public async Task Two_connections_do_not_share_their_pages_nor_their_dialogs()
    {
        await using var provider = Services();
        var sessions = provider.GetRequiredService<RouteableSessions>();
        var clientA = new RecordingClient();
        var clientB = new RecordingClient();
        var a = Connect(provider, sessions, "a", clientA);
        var b = Connect(provider, sessions, "b", clientB);
        await a.OnConnectedAsync();
        await b.OnConnectedAsync();

        Assert.True((await a.GoTo("/shop")).Success);
        Assert.Single(clientA.Navigated);
        Assert.Empty(clientB.Navigated);

        Assert.True((await a.Invoke("askCommand", [])).Success);
        Assert.Single(clientA.Dialogs);
        Assert.Empty(clientB.Dialogs);
        Assert.False(b.Answer(clientA.Dialogs[0].Id, 0).Success);   // the dialog of the other connection
        Assert.True(a.Answer(clientA.Dialogs[0].Id, 0).Success);

        await a.OnDisconnectedAsync(null);
        await b.OnDisconnectedAsync(null);
    }

    [Fact]
    public async Task The_methods_of_the_hub_reach_the_session()
    {
        await using var provider = Services();
        var sessions = provider.GetRequiredService<RouteableSessions>();
        var client = new RecordingClient();
        var hub = Connect(provider, sessions, "c", client);
        await hub.OnConnectedAsync();

        Assert.True((await hub.GoTo("/shop")).Success);
        Assert.True((await hub.Invoke("items.0.buyCommand", [])).Success);
        Assert.True(hub.Set("result", Json("\"typed\"")).Success);
        Assert.Contains(client.Changed, c => c.Property == "result" && Equals(c.Value, "typed"));
        Assert.True((await hub.InvokeOn("", "items.0.buyCommand", [])).Success);
        Assert.True(hub.SetOn("", "result", Json("\"again\"")).Success);

        Assert.True(hub.Watch("$menu").Success);
        Assert.Contains(client.Navigated, n => n.Target == "$menu");
        Assert.True(hub.Watch("side").Success);
        Assert.True((await hub.GoToIn("side", "/shop")).Success);
        Assert.Contains(client.Navigated, n => n.Target == "side");
        Assert.True(hub.Unwatch("side").Success);

        Assert.True((await hub.Invoke("notifyCommand", [])).Success);
        Assert.True(hub.Dismiss(client.Toasts[0].Id, click: false).Success);

        Assert.False((await hub.GoTo("/nowhere")).Success);
        await hub.OnDisconnectedAsync(null);
    }

    [Fact]
    public async Task A_call_after_the_session_is_closed_says_so()
    {
        await using var provider = Services();
        var sessions = provider.GetRequiredService<RouteableSessions>();
        var hub = Connect(provider, sessions, "gone", new RecordingClient());

        // No OnConnectedAsync: the connection has no session (it was closed).
        Assert.Throws<HubException>(() => hub.Set("x", Json("1")));
        await Assert.ThrowsAsync<HubException>(() => hub.GoTo("/shop"));
    }
}
