using System.ComponentModel;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Routeables.Abstraction;
using Routeables.Core;
using Routeables.Services;
using Routeables.SignalR;

namespace Routeables.Tests.Remote;

public sealed class SecretsView;

[RouteableState]
public sealed class SecretsViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title => "Secrets";

    /// <summary>A getter that fails with a message the visitor must not read.</summary>
    public string Broken => throw new IOException("Server=db01;Password=hunter2");

    /// <summary>A getter that fails with a message meant for the visitor.</summary>
    public string Polite => throw new RouteableException("The report is being prepared.");

    [RouteableAuthorize(Policy = "vip")]
    public string Offer => "40% off";

    public void Touch() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
}

[RouteableState(Settable = true)]
public sealed class SettableFormViewModel
{
    public string Name { get; set; } = "";

    [RouteableState(Settable = false)]
    public string AccountId { get; set; } = "";
}

public class DuplicateCommandsViewModel
{
    [RouteableCommand(Name = "delete")]
    [Authorize(Roles = "admin")]
    public void DeleteAll() { }

    [RouteableCommand(Name = "delete")]
    public void DeleteOne() { }
}

public class BaseCommandsViewModel
{
    [RouteableCommand]
    public void Save() { }
}

public class DerivedCommandsViewModel : BaseCommandsViewModel
{
    [RouteableCommand]
    public new void Save() { }
}

/// <summary>The fixes of the audit of 8 November 2026: what a client must never get, and what a server must never keep.</summary>
public class HardeningTests
{
    private static bool _vip;

    private static ServiceProvider Services(Action<RouteablesOptions>? options = null)
    {
        var services = TestSchema.Services(options);
        services.AddAuthorizationCore(o => o.AddPolicy("vip", p => p.RequireAssertion(_ => Volatile.Read(ref _vip))));
        services.RegisterRoute<SecretsView, SecretsViewModel>(route: "/secrets");
        return services.BuildServiceProvider();
    }

    private static RouteablesHub Hub(ServiceProvider provider, string connection, IRouteableClient client, ClaimsPrincipal? user = null, FakeHubContext? context = null)
        => new(provider, provider.GetRequiredService<RouteableSessions>())
        {
            Context = context ?? new FakeHubContext(connection, user),
            Clients = new FakeHubClients(client)
        };

    private static ClaimsPrincipal User(string? id, string issuer = "https://login.example")
        => new(new ClaimsIdentity(id is null ? [new Claim(ClaimTypes.Role, "user", null, issuer)] : [new Claim(ClaimTypes.NameIdentifier, id, null, issuer)], "Bearer"));

    // ---------------------------------------------------------------- what is sent

    [Fact]
    public async Task What_a_getter_throws_reaches_the_client_in_development_only()
    {
        foreach (var detailed in new[] { false, true })
        {
            await using var provider = Services(o => o.DetailedErrors = detailed);
            var client = new RecordingClient();
            await using var session = new RouteableSession(provider, client);
            await session.GoToAsync("/secrets", TestContext.Current.CancellationToken);
            await session.Dispatcher.IdleAsync();

            var broken = Assert.Single(client.Failed, f => f.Message.Contains("broken", StringComparison.Ordinal));
            Assert.Equal(detailed, broken.Message.Contains("hunter2", StringComparison.Ordinal));
            // A RouteableException is meant for the visitor: always sent.
            Assert.Contains(client.Failed, f => f.Message == "The report is being prepared.");
        }
    }

    [Fact]
    public async Task A_permission_withdrawn_while_a_page_is_open_hides_what_it_opened_once_the_sessions_are_authorized_again()
    {
        Volatile.Write(ref _vip, true);
        try
        {
            await using var provider = Services();
            var client = new RecordingClient();
            await using var session = new RouteableSession(provider, client);
            await session.GoToAsync("/secrets", TestContext.Current.CancellationToken);
            await session.Dispatcher.IdleAsync();
            Assert.Equal("40% off", client.Navigated[^1].State["offer"]);

            Volatile.Write(ref _vip, false);
            await provider.GetRequiredService<RouteableSessions>().ReauthorizeAsync();   // nothing: this session has no connection in the registry
            await session.ReauthorizeAsync();
            await session.Dispatcher.IdleAsync();
            Assert.True(await TestSchema.EventuallyAsync(() => !client.Navigated[^1].State.ContainsKey("offer")));
        }
        finally { Volatile.Write(ref _vip, false); }
    }

    // ---------------------------------------------------------------- the model

    [Fact]
    public void A_property_that_says_it_may_not_be_set_wins_over_its_class()
    {
        var shape = TestSchema.Schema.SettableProperties(typeof(SettableFormViewModel)).Select(p => p.Name).ToList();
        Assert.Equal(["Name"], shape);
    }

    [Fact]
    public void Two_commands_with_one_name_are_refused_and_a_method_hidden_by_new_is_replaced()
    {
        var refused = Assert.Throws<InvalidOperationException>(() => TestSchema.Schema.Commands(typeof(DuplicateCommandsViewModel)));
        Assert.Contains("'delete'", refused.Message);

        var save = Assert.Single(TestSchema.Schema.Commands(typeof(DerivedCommandsViewModel)));
        Assert.Equal(("save", typeof(DerivedCommandsViewModel)), (save.Key, save.Value.DeclaringType));
    }

    [Fact]
    public async Task A_dialog_without_buttons_is_answered_with_0_only_and_open_notifications_are_bounded()
    {
        var interactions = new RemoteInteractions();
        RouteableDialog? asked = null;
        interactions.DialogRequested += d => asked = d;
        var answer = interactions.AskAsync("message", "Done", null, "Saved", null, false, []);
        Assert.False(interactions.Answer(asked!.Id, 1));
        Assert.True(interactions.Answer(asked.Id, 0));
        Assert.Equal(0, await answer);

        for (var i = 0; i < 100; i++)
            interactions.Notify("t", "m", RouteableNotificationType.Information, null, null, null);
        Assert.False(interactions.Dismiss(1, click: false));     // the oldest were forgotten
        Assert.True(interactions.Dismiss(100, click: false));
    }

    // ---------------------------------------------------------------- the sessions

    [Fact]
    public void A_user_without_a_stable_identifier_cannot_resume_and_two_issuers_are_two_users()
    {
        Assert.Null(RouteableSession.KeyOf(User(null)));
        Assert.Equal(string.Empty, RouteableSession.KeyOf(null));
        Assert.NotEqual(RouteableSession.KeyOf(User("42", "https://a.example")), RouteableSession.KeyOf(User("42", "https://b.example")));
        Assert.Equal(RouteableSession.KeyOf(User("42")), RouteableSession.KeyOf(User("42")));
    }

    [Fact]
    public async Task The_sessions_that_wait_are_bounded_overall_and_per_user()
    {
        await using var provider = Services(o => { o.MaxDetachedSessions = 3; o.MaxDetachedSessionsPerUser = 2; });
        var sessions = provider.GetRequiredService<RouteableSessions>();
        var tokens = new List<string>();
        async Task DropAsync(string connection, ClaimsPrincipal? user)
        {
            var hub = Hub(provider, connection, new RecordingClient(), user);
            await hub.OnConnectedAsync();
            tokens.Add((await hub.Hello(new RouteableHello(RouteableProtocol.Version))).Session!);
            await hub.OnDisconnectedAsync(null);
        }

        var ada = User("ada");
        await DropAsync("a1", ada);
        await DropAsync("a2", ada);
        await DropAsync("a3", ada);   // Ada's first session leaves
        Assert.Equal(2, sessions.DetachedCount);
        await DropAsync("x1", null);
        await DropAsync("x2", null);  // the oldest overall leaves
        Assert.Equal(3, sessions.DetachedCount);

        var back = Hub(provider, "back", new RecordingClient(), ada);
        await back.OnConnectedAsync();
        Assert.False((await back.Hello(new RouteableHello(RouteableProtocol.Version, tokens[0]))).Resumed);
        Assert.True((await back.Hello(new RouteableHello(RouteableProtocol.Version, tokens[2]))).Resumed);
    }

    [Fact]
    public async Task A_client_that_comes_back_before_the_server_noticed_it_left_gets_its_session_and_the_old_connection_is_closed()
    {
        await using var provider = Services();
        var sessions = provider.GetRequiredService<RouteableSessions>();
        var ada = User("ada");
        var oldContext = new FakeHubContext("old", ada);
        var old = Hub(provider, "old", new RecordingClient(), context: oldContext);
        await old.OnConnectedAsync();
        var welcome = await old.Hello(new RouteableHello(RouteableProtocol.Version));
        await old.GoTo("/secrets");

        // The client already reconnected; the server still holds the old connection.
        var after = new RecordingClient();
        var fresh = Hub(provider, "new", after, ada);
        await fresh.OnConnectedAsync();
        var resumed = await fresh.Hello(new RouteableHello(RouteableProtocol.Version, welcome.Session));
        Assert.True(resumed.Resumed);
        Assert.True(oldContext.Aborted);
        Assert.Equal("/secrets", after.Navigated[^1].Route);

        // The end of the old connection does not touch the session it lost.
        await old.OnDisconnectedAsync(new IOException("timeout"));
        Assert.Equal(1, sessions.Count);
        Assert.True((await fresh.GoTo("/secrets")).Success);

        // Another user with the token gets nothing.
        var eve = Hub(provider, "eve", new RecordingClient(), User("eve"));
        await eve.OnConnectedAsync();
        Assert.False((await eve.Hello(new RouteableHello(RouteableProtocol.Version, welcome.Session))).Resumed);
        await fresh.OnDisconnectedAsync(null);
        await eve.OnDisconnectedAsync(null);
    }

    [Fact]
    public async Task A_modal_opened_from_a_modal_covers_it_and_shows_it_again_when_it_closes()
    {
        await using var provider = Services();
        var client = new RecordingClient();
        await using var session = new RouteableSession(provider, client);
        var interactions = session.Services.GetRequiredService<RemoteInteractions>();
        var outer = new SettableFormViewModel { Name = "outer" };
        var inner = new SettableFormViewModel { Name = "inner" };
        Action<string?>? closeOuter = null, closeInner = null;

        var outerTask = session.Dispatcher.InvokeAsync(() => interactions.ShowModalAsync<string>(outer, close => { closeOuter = close; return new Unsubscribe(); }));
        var innerTask = session.Dispatcher.InvokeAsync(() => interactions.ShowModalAsync<string>(inner, close => { closeInner = close; return new Unsubscribe(); }));
        Assert.True(await TestSchema.EventuallyAsync(() => client.Navigated.Count(n => n.Target == RouteableTargets.Modal) == 2));
        Assert.Equal("inner", client.Navigated[^1].State["name"]);

        closeInner!("done");
        Assert.True(await TestSchema.EventuallyAsync(() => client.Navigated.Count(n => n.Target == RouteableTargets.Modal) == 3));
        Assert.Equal("outer", client.Navigated[^1].State["name"]);   // still awaited by its command: shown again
        Assert.Empty(client.Closed);

        closeOuter!("done");
        Assert.True(await TestSchema.EventuallyAsync(() => client.Closed.Count == 1));
        await outerTask;
        await innerTask;
    }

    private sealed class Unsubscribe : IDisposable
    {
        public void Dispose() { }
    }
}
