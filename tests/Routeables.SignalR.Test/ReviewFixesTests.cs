using System.Diagnostics;
using System.Text.Json;
using System.Windows.Input;
using Routeables.Abstraction;
using Routeables.Abstraction.Lifecycle;
using Routeables.Abstraction.Registrations;
using Routeables.Services;
using Routeables.SignalR;

namespace Routeables.Tests.Remote;

/// <summary>A command typed as the RelayCommand&lt;T&gt; of the MVVM Toolkit: it refuses an argument that is not a <typeparamref name="T"/>.</summary>
public sealed class TypedCommand<T>(Action<T> run) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => parameter is T || (parameter is null && default(T) is null);
    public void Execute(T parameter) => run(parameter);
    public void Execute(object? parameter) => Execute(parameter is T value ? value : throw new ArgumentException($"The argument is not a {typeof(T).Name}."));
}

/// <summary>A command typed as the AsyncRelayCommand&lt;T&gt; of the MVVM Toolkit.</summary>
public sealed class TypedAsyncCommand<T>(Func<T, Task> run) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => parameter is T || (parameter is null && default(T) is null);
    public Task ExecuteAsync(T parameter) => run(parameter);
    public async void Execute(object? parameter) => await ExecuteAsync((T)parameter!);
}

/// <summary>A command that only knows ICommand: it takes whatever is sent.</summary>
public sealed class LooseCommand(Action<object?> run) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => run(parameter);
}

public sealed record Pick(int Id, string Name);

[RouteableState]
public sealed class PickerViewModel
{
    public PickerViewModel()
    {
        PickCommand = new TypedCommand<int>(n => Received.Add(n));
        IdCommand = new TypedCommand<long>(n => Received.Add(n));
        OrderCommand = new TypedAsyncCommand<Pick>(p => { Received.Add(p); return Task.CompletedTask; });
        LooseCommand = new LooseCommand(Received.Add);
    }

    public ICommand PickCommand { get; }
    public ICommand IdCommand { get; }
    public ICommand OrderCommand { get; }
    public ICommand LooseCommand { get; }

    internal List<object?> Received { get; } = [];
}

/// <summary>A page that asks before it lets the visitor leave, and stops asking when the route change is cancelled.</summary>
[RouteableState]
public sealed class CarefulViewModel(IRouteableDialogService dialogs) : IRouteable
{
    public string Name => "careful";

    public Task<bool> CanLeaveAsync(CancellationToken ct = default) => dialogs.ShowYesNoAsync("Leave without saving?", "Leave").WaitAsync(ct);
}

[RouteableState]
public sealed class BlockingViewModel
{
    /// <summary>Long before its first await, as a command that calls something synchronous.</summary>
    [RouteableCommand]
    public void Block(int milliseconds) => Thread.Sleep(milliseconds);
}

/// <summary>The points of the review of 15 November 2026.</summary>
public sealed class ReviewFixesTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static ServiceProvider Services(Action<RouteablesOptions>? options = null)
    {
        var services = TestSchema.Services(options);
        services.RegisterRoute<StreamView, StreamViewModel>(route: "/stream");
        services.RegisterRoute<StreamView, GuardedViewModel>(route: "/guarded");
        services.RegisterRoute<StreamView, BlockingViewModel>(route: "/blocking");
        services.RegisterRoute<StreamView, CarefulViewModel>(route: "/careful");
        return services.BuildServiceProvider();
    }

    // Opens a page and waits for the end of the route change. On a cold runner it may outlast a short grace: it is then a call that goes on,
    // which counts against MaxRunningCalls and still runs when the test goes on.
    private static async Task OpenAsync(RouteableSession session, RecordingClient client, string route)
    {
        var opened = await session.GoToAsync(route, TestContext.Current.CancellationToken);
        Assert.True(opened.Success, opened.Error);
        if (opened.Pending)
            Assert.True(await TestSchema.EventuallyAsync(() => client.Completed.Any(c => c.Operation == opened.Operation && c.Success)));
    }

    // ---------------------------------------------------------------- 1: the argument of a command of the state

    [Fact]
    public async Task A_typed_command_gets_its_argument_in_its_own_type()
    {
        var picker = new PickerViewModel();

        Assert.True((await TestSchema.Invoker.InvokeAsync(picker, "pickCommand", [Json("42")])).Success);
        Assert.True((await TestSchema.Invoker.InvokeAsync(picker, "idCommand", [Json("9007199254740993")])).Success);
        Assert.True((await TestSchema.Invoker.InvokeAsync(picker, "orderCommand", [Json("""{"id":3,"name":"tea"}""")])).Success);

        Assert.Equal(42, Assert.IsType<int>(picker.Received[0]));
        Assert.Equal(9007199254740993L, Assert.IsType<long>(picker.Received[1]));   // no precision lost on the way through a double
        Assert.Equal(new Pick(3, "tea"), picker.Received[2]);
    }

    [Fact]
    public async Task A_command_that_takes_anything_gets_the_closest_value_and_an_object_as_it_was_sent()
    {
        var picker = new PickerViewModel();

        foreach (var json in new[] { "7", "3000000000", "1.5", "\"x\"", "true", """{"a":1}""" })
            Assert.True((await TestSchema.Invoker.InvokeAsync(picker, "looseCommand", [Json(json)])).Success);

        Assert.IsType<int>(picker.Received[0]);
        Assert.Equal(3000000000L, Assert.IsType<long>(picker.Received[1]));
        Assert.Equal(1.5, Assert.IsType<double>(picker.Received[2]));
        Assert.Equal("x", picker.Received[3]);
        Assert.Equal(true, picker.Received[4]);
        Assert.Equal(1, Assert.IsType<JsonElement>(picker.Received[5]).GetProperty("a").GetInt32());   // not null any more
    }

    [Fact]
    public async Task An_argument_that_is_not_of_the_type_of_the_command_is_refused_as_such()
    {
        var picker = new PickerViewModel();

        var ack = await TestSchema.Invoker.InvokeAsync(picker, "pickCommand", [Json("\"abc\"")]);

        Assert.False(ack.Success);
        Assert.Equal(RouteableErrorCodes.InvalidArgument, ack.Code);
        Assert.Empty(picker.Received);
    }

    // ---------------------------------------------------------------- 2: a route change and the connection that drops

    [Fact]
    public async Task A_route_change_that_waits_for_the_visitor_survives_the_connection_that_drops()
    {
        await using var provider = Services();
        var sessions = provider.GetRequiredService<RouteableSessions>();
        var context = new FakeHubContext("c1");
        var client = new RecordingClient();
        var hub = new RouteablesHub(provider, sessions) { Context = context, Clients = new FakeHubClients(client) };
        await hub.OnConnectedAsync();
        await hub.Hello(new RouteableHello(RouteableProtocol.Version));
        Assert.True((await hub.GoTo("/careful")).Success);

        var ack = await hub.GoTo("/stream");
        Assert.True(ack.Pending);   // the page asks whether to leave
        context.Abort();            // and the connection drops meanwhile: the session waits for its client

        var session = sessions.Get("c1")!;
        Assert.True((await session.AnswerAsync(Assert.Single(client.Dialogs).Id, 0)).Success);
        Assert.True(await TestSchema.EventuallyAsync(() => client.Completed.Any(c => c.Operation == ack.Operation)));
        Assert.True(client.Completed.Single(c => c.Operation == ack.Operation).Success);
        Assert.IsType<StreamViewModel>(session.Current);
    }

    // ---------------------------------------------------------------- 4: the calls that go on are bounded

    [Fact]
    public async Task A_session_refuses_a_call_beyond_the_calls_that_may_go_on_until_one_ends()
    {
        await using var provider = Services(o => { o.MaxRunningCalls = 1; o.CommandGrace = TimeSpan.FromMilliseconds(50); });
        var client = new RecordingClient();
        await using var session = new RouteableSession(provider, client);
        await OpenAsync(session, client, "/stream");

        var first = await session.InvokeAsync("slowAsync", [Json("400")]);
        Assert.True(first.Pending);

        var refused = await session.InvokeAsync("slowAsync", [Json("1")]);
        Assert.False(refused.Success);
        Assert.Equal(RouteableErrorCodes.Limit, refused.Code);

        Assert.True(await TestSchema.EventuallyAsync(() => client.Completed.Any(c => c.Operation == first.Operation)));
        var after = await session.InvokeAsync("slowAsync", [Json("1")]);
        Assert.NotEqual(RouteableErrorCodes.Limit, after.Code);
        Assert.True(after.Success || after.Pending);
    }

    [Fact]
    public void The_limit_of_the_calls_that_go_on_is_at_least_one()
    {
        var options = new RouteablesOptions { MaxRunningCalls = 0 };
        Assert.Contains(options.Problems(), p => p.Contains(nameof(RouteablesOptions.MaxRunningCalls), StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- 6: the grace of a command long before its first await

    [Fact]
    public async Task A_command_long_before_its_first_await_is_answered_as_going_on_after_the_grace()
    {
        await using var provider = Services(o => o.CommandGrace = TimeSpan.FromMilliseconds(100));
        var client = new RecordingClient();
        await using var session = new RouteableSession(provider, client);
        await OpenAsync(session, client, "/blocking");

        var watch = Stopwatch.StartNew();
        var ack = await session.InvokeAsync("block", [Json("1500")]);
        watch.Stop();

        Assert.True(ack.Pending);
        Assert.True(watch.ElapsedMilliseconds < 1000, $"answered after {watch.ElapsedMilliseconds} ms");
        Assert.True(await TestSchema.EventuallyAsync(() => client.Completed.Any(c => c.Operation == ack.Operation && c.Success)));
    }
}