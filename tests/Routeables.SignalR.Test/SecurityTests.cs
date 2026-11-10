using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using System.Windows.Input;
using Microsoft.AspNetCore.Authorization;
using Routeables.Abstraction;
using Routeables.Abstraction.Registrations;
using Routeables.Core;
using Routeables.SignalR;

namespace Routeables.Tests.Remote;

public sealed class OrdersView;

[RouteableState]
public sealed class OrdersViewModel : INotifyPropertyChanged
{
    private string _status = "open";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Status { get => _status; private set { _status = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status))); } }

    /// <summary>A property of the page that only managers see.</summary>
    [RouteableAuthorize(Roles = "manager")]
    public decimal Margin => 0.42m;

    /// <summary>A public setter that is not settable: the class does not say Settable.</summary>
    public bool IsAdmin { get; set; }

    [RouteableState(Settable = true)]
    public string Search { get; set; } = string.Empty;

    [RouteableAuthorize(Roles = "manager"), RouteableState(Settable = true)]
    public decimal Discount { get; set; }

    [RouteableAuthorize(Roles = "manager")]
    public ICommand PurgeCommand { get; } = new TestCommand(() => Task.CompletedTask);

    [RouteableCommand]
    public void View() => Status = "viewed";

    [RouteableCommand, Authorize(Roles = "manager")]
    public void Approve() => Status = "approved";

    [RouteableCommand, Authorize(Policy = "Auditors")]
    public void Audit() => Status = "audited";
}

/// <summary>A page that only managers may open.</summary>
[RouteableState, Authorize(Roles = "manager")]
public sealed class AdminViewModel
{
    public string Secret => "the margin of every order";

    [RouteableCommand, AllowAnonymous]
    public void Ping() { }
}

/// <summary>Commands that fail in every way a command can.</summary>
[RouteableState]
public sealed class FaultyViewModel
{
    public int Calls { get; private set; }

    public CancellationToken Lifetime { get; private set; }

    [RouteableCommand] public async Task FailLaterAsync() { await Task.Yield(); throw new InvalidOperationException("the database is down"); }
    [RouteableCommand] public Task FailNowAsync() => throw new InvalidOperationException("thrown before the first await");
    [RouteableCommand] public void Refuse() => throw new RouteableException("The order is already shipped.");
    [RouteableCommand] public async Task CancelAsync() { await Task.Yield(); throw new OperationCanceledException(); }
    [RouteableCommand] public async ValueTask CountAsync() { await Task.Yield(); Calls++; }
    [RouteableCommand] public async ValueTask<int> CountAndReturnAsync() { await Task.Yield(); return ++Calls; }
    [RouteableCommand] public void Take(IReadOnlyList<int> values, int factor) => Calls += values.Sum() * factor;
    [RouteableCommand] public void TakeInterface(IDisposable value) => Calls++;
    [RouteableCommand] public async Task WaitAsync(CancellationToken cancellationToken) { Lifetime = cancellationToken; await Task.Delay(Timeout.Infinite, cancellationToken); }
}

/// <summary>What a user may see and do: authorization, exposure, and what a client sends.</summary>
public class SecurityTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static ClaimsPrincipal User(string name, params string[] roles)
        => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, name), new Claim(ClaimTypes.Name, name), .. roles.Select(r => new Claim(ClaimTypes.Role, r))], "test"));

    private static ServiceProvider Services(Action<RouteablesOptions>? options = null)
    {
        var services = TestSchema.Services(options);
        services.AddAuthorizationCore(o => o.AddPolicy("Auditors", p => p.RequireClaim("department", "audit")));
        services.RegisterRoute<OrdersView, OrdersViewModel>(route: "/orders");
        services.RegisterRoute<OrdersView, AdminViewModel>(route: "/admin");
        services.RegisterRoute<OrdersView, FaultyViewModel>(route: "/faulty");
        services.AddRouteableTarget<AdminViewModel>("$admin");
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task A_command_is_authorized_at_every_call_for_the_user_of_the_connection()
    {
        await using var provider = Services();
        var clerkClient = new RecordingClient();
        var managerClient = new RecordingClient();
        await using var clerk = new RouteableSession(provider, clerkClient, User("clerk"));
        await using var manager = new RouteableSession(provider, managerClient, User("boss", "manager"));
        await clerk.GoToAsync("/orders", TestContext.Current.CancellationToken);
        await manager.GoToAsync("/orders", TestContext.Current.CancellationToken);

        Assert.True((await clerk.InvokeAsync("view", [])).Success);
        var refused = await clerk.InvokeAsync("approve", []);
        Assert.False(refused.Success);
        Assert.Equal(RouteableErrorCodes.Unauthorized, refused.Code);
        Assert.Equal("viewed", ((OrdersViewModel)clerk.Current!).Status);

        Assert.True((await manager.InvokeAsync("approve", [])).Success);
        Assert.Equal("approved", ((OrdersViewModel)manager.Current!).Status);

        // A policy the user does not meet, and a command the user does not see in the list of the page.
        Assert.Equal(RouteableErrorCodes.Unauthorized, (await manager.InvokeAsync("audit", [])).Code);
        Assert.DoesNotContain("approve", clerkClient.Navigated[0].Commands);
        Assert.Contains("approve", managerClient.Navigated[0].Commands);
    }

    [Fact]
    public async Task An_anonymous_connection_meets_no_authorize_attribute()
    {
        await using var provider = Services();
        await using var session = new RouteableSession(provider, new RecordingClient());
        await session.GoToAsync("/orders", TestContext.Current.CancellationToken);

        Assert.Equal(RouteableErrorCodes.Unauthorized, (await session.InvokeAsync("approve", [])).Code);
        Assert.True((await session.InvokeAsync("view", [])).Success);
    }

    [Fact]
    public async Task A_property_that_asks_more_than_the_page_is_neither_sent_nor_set_for_who_does_not_meet_it()
    {
        await using var provider = Services();
        var clerkClient = new RecordingClient();
        var managerClient = new RecordingClient();
        await using var clerk = new RouteableSession(provider, clerkClient, User("clerk"));
        await using var manager = new RouteableSession(provider, managerClient, User("boss", "manager"));
        await clerk.GoToAsync("/orders", TestContext.Current.CancellationToken);
        await manager.GoToAsync("/orders", TestContext.Current.CancellationToken);

        Assert.DoesNotContain("margin", clerkClient.Navigated[0].State.Keys);
        Assert.DoesNotContain("purgeCommand", clerkClient.Navigated[0].State.Keys);
        Assert.Equal(0.42m, managerClient.Navigated[0].State["margin"]);

        Assert.Equal(RouteableErrorCodes.Unauthorized, (await clerk.SetAsync("discount", Json("10"))).Code);
        Assert.True((await manager.SetAsync("discount", Json("10"))).Success);
        Assert.Equal(10m, ((OrdersViewModel)manager.Current!).Discount);

        Assert.Equal(RouteableErrorCodes.Unauthorized, (await clerk.InvokeAsync("purgeCommand", [])).Code);
        Assert.True((await manager.InvokeAsync("purgeCommand", [])).Success);
    }

    [Fact]
    public async Task A_public_setter_is_not_settable_unless_it_says_so()
    {
        await using var provider = Services();
        await using var session = new RouteableSession(provider, new RecordingClient(), User("mallory"));
        await session.GoToAsync("/orders", TestContext.Current.CancellationToken);

        var ack = await session.SetAsync("isAdmin", Json("true"));

        Assert.False(ack.Success);
        Assert.Equal(RouteableErrorCodes.NotFound, ack.Code);
        Assert.False(((OrdersViewModel)session.Current!).IsAdmin);
        Assert.True((await session.SetAsync("search", Json("\"tea\""))).Success);
    }

    [Fact]
    public async Task The_public_exposure_of_the_first_versions_is_an_explicit_choice()
    {
        var schema = new RouteableSchema(new RouteablesOptions { Exposure = RouteableExposure.Public });
        var state = new RouteableStateProjector(schema).Project(new CounterViewModel());
        Assert.Contains("secret", state.Keys);

        var invoker = new RouteableCommandInvoker(schema);
        var viewModel = new CounterViewModel();
        Assert.True((await invoker.SetAsync(viewModel, "secret", Json("\"changed\""))).Success);
        Assert.Equal("changed", viewModel.Secret);
    }

    [Fact]
    public async Task Types_that_cannot_be_annotated_are_exposed_by_a_convention()
    {
        var options = new RouteablesOptions().ExposeTypes(t => t == typeof(MenuItemLike), settable: true);
        var schema = new RouteableSchema(options);

        var state = new RouteableStateProjector(schema).Project(new MenuItemLike());
        Assert.Equal("Home", state["label"]);
        Assert.True((await new RouteableCommandInvoker(schema).SetAsync(new MenuItemLike(), "label", Json("\"Start\""))).Success);
    }

    public sealed class MenuItemLike
    {
        public string Label { get; set; } = "Home";
    }

    [Fact]
    public async Task A_page_the_user_may_not_see_is_not_opened_and_never_sent()
    {
        await using var provider = Services();
        var client = new RecordingClient();
        await using var session = new RouteableSession(provider, client, User("clerk"));

        var ack = await session.GoToAsync("/admin", TestContext.Current.CancellationToken);

        Assert.False(ack.Success);   // the guard cancelled the route change
        Assert.Null(session.Current);
        Assert.DoesNotContain(client.Navigated, n => n.ViewModel == nameof(AdminViewModel));

        // A registered view model is not sent either.
        await session.WatchAsync("$admin");
        Assert.DoesNotContain(client.Navigated, n => n.Target == "$admin");
        Assert.Contains(client.Failed, f => f.Target == "$admin" && f.Code == RouteableErrorCodes.Unauthorized);
        Assert.Equal(RouteableErrorCodes.NotFound, (await session.InvokeOnAsync("$admin", "ping", [])).Code);

        var managerClient = new RecordingClient();
        await using var manager = new RouteableSession(provider, managerClient, User("boss", "manager"));
        Assert.True((await manager.GoToAsync("/admin", TestContext.Current.CancellationToken)).Success);
        Assert.Equal("the margin of every order", managerClient.Navigated.Single(n => n.ViewModel == nameof(AdminViewModel)).State["secret"]);
    }

    [Fact]
    public async Task What_a_client_sends_never_makes_the_hub_throw()
    {
        await using var provider = Services(o => { o.MaxArgumentLength = 1000; o.MaxArgumentItems = 50; o.MaxArgumentDepth = 5; });
        await using var session = new RouteableSession(provider, new RecordingClient());
        await session.GoToAsync("/faulty", TestContext.Current.CancellationToken);

        var cases = new (string Command, JsonElement[] Arguments)[]
        {
            ("take", [Json("[" + string.Join(',', Enumerable.Repeat(1, 400)) + "]"), Json("1")]),   // too long
            ("take", [Json("[" + string.Join(',', Enumerable.Repeat(1, 60)) + "]"), Json("1")]),    // too many values
            ("take", [Json("[[[[[[[1]]]]]]]"), Json("1")]),                                       // too deep
            ("take", [Json("[1]"), Json("1"), Json("1")]),                                         // too many arguments
            ("take", [Json("\"x\""), Json("1")]),                                                  // wrong type
            ("take", [Json("[1]"), Json("null")]),                                                 // null for an int
            ("take", [Json("[1]")]),                                                               // missing argument
            ("takeInterface", [Json("{}")]),                                                       // a type JSON cannot create
            ("nothing", []),                                                                       // unknown
            (new string('x', 600), []),                                                            // name too long
            ("calls.x.y.z.a.b.c.d.e.f.g.h.i.j", []),                                               // a path too long
        };
        foreach (var (command, arguments) in cases)
        {
            var ack = await session.InvokeAsync(command, arguments);
            Assert.False(ack.Success, command.Length > 40 ? "long name" : command);
            Assert.NotNull(ack.Error);
        }

        Assert.Equal(RouteableErrorCodes.InvalidArgument, (await session.SetAsync("calls", Json(new string('1', 2000)))).Code);
        Assert.True((await session.InvokeAsync("take", [Json("[1,2,3]"), Json("2")])).Success);
        Assert.Equal(12, ((FaultyViewModel)session.Current!).Calls);
    }

    [Fact]
    public async Task Every_failure_of_a_command_is_an_answer_and_unexpected_messages_stay_on_the_server()
    {
        await using var detailed = Services();
        await using var session = new RouteableSession(detailed, new RecordingClient());
        await session.GoToAsync("/faulty", TestContext.Current.CancellationToken);

        var later = await session.InvokeAsync("failLaterAsync", []);
        Assert.False(later.Success);
        Assert.Equal("the database is down", later.Error);
        Assert.Equal("thrown before the first await", (await session.InvokeAsync("failNowAsync", [])).Error);
        Assert.Equal(RouteableErrorCodes.Cancelled, (await session.InvokeAsync("cancelAsync", [])).Code);

        await using var production = Services(o => o.DetailedErrors = false);
        await using var hidden = new RouteableSession(production, new RecordingClient());
        await hidden.GoToAsync("/faulty", TestContext.Current.CancellationToken);
        Assert.Equal("The command failed.", (await hidden.InvokeAsync("failLaterAsync", [])).Error);
        Assert.Equal("The order is already shipped.", (await hidden.InvokeAsync("refuse", [])).Error);
    }

    [Fact]
    public async Task A_command_that_returns_a_value_task_is_awaited()
    {
        await using var provider = Services();
        await using var session = new RouteableSession(provider, new RecordingClient());
        await session.GoToAsync("/faulty", TestContext.Current.CancellationToken);

        Assert.True((await session.InvokeAsync("countAsync", [])).Success);
        Assert.True((await session.InvokeAsync("countAndReturnAsync", [])).Success);
        Assert.Equal(2, ((FaultyViewModel)session.Current!).Calls);
    }

    [Fact]
    public async Task A_command_that_takes_a_token_is_cancelled_when_the_session_ends_and_the_client_is_told_when_it_ends()
    {
        await using var provider = Services(o => o.CommandGrace = TimeSpan.FromMilliseconds(50));
        var client = new RecordingClient();
        var session = new RouteableSession(provider, client);
        await session.GoToAsync("/faulty", TestContext.Current.CancellationToken);
        var viewModel = (FaultyViewModel)session.Current!;

        var ack = await session.InvokeAsync("waitAsync", []);
        Assert.True(ack.Pending);
        Assert.NotNull(ack.Operation);
        Assert.True(viewModel.Lifetime.CanBeCanceled);

        await session.DisposeAsync();
        Assert.True(viewModel.Lifetime.IsCancellationRequested);
    }

    [Fact]
    public async Task A_connection_that_must_be_authenticated_is_closed_when_it_is_not()
    {
        await using var provider = Services(o => o.RequireAuthenticatedUser = true);
        var sessions = provider.GetRequiredService<RouteableSessions>();
        var anonymous = new FakeHubContext("anonymous");
        var hub = new RouteablesHub(provider, sessions) { Context = anonymous, Clients = new FakeHubClients(new RecordingClient()) };

        await hub.OnConnectedAsync();

        Assert.True(anonymous.Aborted);
        Assert.Equal(0, sessions.Count);

        var signedIn = new FakeHubContext("signed-in", User("ann"));
        hub = new RouteablesHub(provider, sessions) { Context = signedIn, Clients = new FakeHubClients(new RecordingClient()) };
        await hub.OnConnectedAsync();
        Assert.False(signedIn.Aborted);
        Assert.Equal(1, sessions.Count);
    }
}
