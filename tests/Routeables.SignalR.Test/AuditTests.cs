using System.Text.Json;
using System.Windows.Input;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Routeables.Abstraction.Registrations;
using Routeables.SignalR;

namespace Routeables.Tests.Remote;

[RouteableState]
public sealed class KeyedViewModel
{
    public KeyedViewModel()
    {
        ByNumber = new() { [7] = new TestCommand(() => { Ran.Add("7"); return Task.CompletedTask; }) };
        ByName = new()
        {
            ["a.b"] = new TestCommand(() => { Ran.Add("a.b"); return Task.CompletedTask; }),
            ["a"] = new TestCommand(() => { Ran.Add("a"); return Task.CompletedTask; }),
        };
    }

    public Dictionary<int, ICommand> ByNumber { get; }
    public Dictionary<string, ICommand> ByName { get; }

    internal List<string> Ran { get; } = [];
}

/// <summary>The points of the audit of 11 November 2026 about the hub and the state.</summary>
public sealed class AuditTests
{
    private static ServiceProvider Services(Action<RouteablesOptions>? options = null)
    {
        var services = TestSchema.Services(options);
        services.RegisterRoute<StreamView, KeyedViewModel>(route: "/keyed");
        services.RegisterRoute<StreamView, StreamViewModel>(route: "/stream");
        return services.BuildServiceProvider();
    }

    // ---------------------------------------------------------------- D7: the keys of a dictionary

    [Fact]
    public async Task A_command_under_a_key_that_is_not_text_or_holds_a_dot_can_be_called()
    {
        await using var provider = Services();
        var client = new RecordingClient();
        await using var session = new RouteableSession(provider, client);
        await session.GoToAsync("/keyed", TestContext.Current.CancellationToken);
        var viewModel = (KeyedViewModel)session.Current!;

        var byNumber = (Dictionary<string, object?>)client.Navigated.Last().State["byNumber"]!;
        Assert.Equal("byNumber.7", ((Dictionary<string, object?>)byNumber["7"]!)["$command"]);

        Assert.True((await session.InvokeAsync("byNumber.7", [])).Success);
        Assert.True((await session.InvokeAsync("byName.a.b", [])).Success);
        Assert.True((await session.InvokeAsync("byName.a", [])).Success);
        Assert.False((await session.InvokeAsync("byName.a.c", [])).Success);
        Assert.True(await TestSchema.EventuallyAsync(() => viewModel.Ran.Count == 3));
        Assert.Equal(["7", "a.b", "a"], viewModel.Ran);
    }

    // ---------------------------------------------------------------- D8: an item held twice

    [Fact]
    public async Task An_item_the_list_holds_twice_is_sent_where_it_is_both_times()
    {
        await using var provider = Services();
        var client = new RecordingClient();
        await using var session = new RouteableSession(provider, client);
        await session.GoToAsync("/stream", TestContext.Current.CancellationToken);
        var viewModel = (StreamViewModel)session.Current!;
        var twice = new Line("x");
        await session.Dispatcher.InvokeAsync(() => { viewModel.Lines.Add(twice); viewModel.Lines.Add(new Line("y")); viewModel.Lines.Add(twice); });
        await session.FlushAsync();

        await session.Dispatcher.InvokeAsync(() => twice.Text = "z");
        await session.FlushAsync();

        var change = client.Changed.Last();
        Assert.Equal("lines", change.Property);   // the whole list: both copies
        var texts = ((IEnumerable<object?>)change.Value!).Select(l => ((Dictionary<string, object?>)l!)["text"]);
        Assert.Equal(["z", "y", "z"], texts);
    }

    // ---------------------------------------------------------------- D11: the size of a message

    [Fact]
    public async Task The_hub_receives_a_message_that_holds_the_longest_argument_the_options_allow()
    {
        await using var provider = Services(o => o.MaxArgumentLength = 100_000);
        var hub = provider.GetRequiredService<IOptions<HubOptions<RouteablesHub>>>().Value;
        Assert.True(hub.MaximumReceiveMessageSize >= 300_000);

        var services = TestSchema.Services();
        services.Configure<HubOptions<RouteablesHub>>(o => o.MaximumReceiveMessageSize = 1_000_000);   // larger: left alone
        await using var larger = services.BuildServiceProvider();
        Assert.Equal(1_000_000, larger.GetRequiredService<IOptions<HubOptions<RouteablesHub>>>().Value.MaximumReceiveMessageSize);
    }

    // ---------------------------------------------------------------- D13: the calls during a Hello that resumes

    [Fact]
    public async Task A_call_that_arrives_while_a_session_is_resumed_waits_for_it()
    {
        await using var provider = Services();
        var sessions = provider.GetRequiredService<RouteableSessions>();
        var hub = new RouteablesHub(provider, sessions) { Context = new FakeHubContext("c1"), Clients = new FakeHubClients(new RecordingClient()) };
        await hub.OnConnectedAsync();
        await hub.Hello(new RouteableHello(RouteableProtocol.Version));

        var greeting = sessions.BeginGreeting("c1");
        var going = hub.GoTo("/stream");
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(going.IsCompleted);

        greeting.Dispose();
        Assert.True((await going).Success);
        Assert.Null(sessions.GreetingOf("c1"));
    }
}
