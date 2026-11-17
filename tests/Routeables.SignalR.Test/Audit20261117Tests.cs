using System.Diagnostics;
using Routeables.Abstraction.Registrations;
using Routeables.SignalR;

namespace Routeables.Tests.Remote;

/// <summary>An object of the state that marks nothing: what the opening example of the README did with its orders.</summary>
public sealed record UnmarkedOrder(int Id, string Customer);

[RouteableState]
public sealed class UnmarkedOrdersViewModel
{
    public List<UnmarkedOrder> Orders { get; } = [new(1, "Ana"), new(2, "Bob")];
}

[RouteableState]
public sealed class CaseTwins
{
    public string URL { get; set; } = "a";
    public string Url { get; set; } = "b";
}

/// <summary>The points of the audit of 17 November 2026 about the hub, the state and the options.</summary>
public sealed class Audit20261117Tests
{
    private static ServiceProvider Services(Action<RouteablesOptions>? options = null)
    {
        var services = TestSchema.Services(options);
        services.RegisterRoute<StreamView, StreamViewModel>(route: "/stream");
        return services.BuildServiceProvider();
    }

    // ---------------------------------------------------------------- B3: objects sent empty

    [Fact]
    public void An_object_of_the_state_that_exposes_nothing_is_still_sent_empty_and_is_told_once()
    {
        var projector = new RouteableStateProjector(new RouteableSchema(new RouteablesOptions()));
        var told = new List<Type>();
        projector.SentEmpty = told.Add;

        var state = projector.Project(new UnmarkedOrdersViewModel());
        projector.Project(new UnmarkedOrdersViewModel());

        var orders = (List<object?>)state["orders"]!;
        Assert.All(orders, o => Assert.Empty((Dictionary<string, object?>)o!));     // nothing leaves without saying so: unchanged
        Assert.Equal([typeof(UnmarkedOrder)], told);                                // but it is said, once
    }

    [Fact]
    public void An_object_is_not_told_empty_when_everything_is_exposed_or_when_it_has_nothing_to_send()
    {
        var projector = new RouteableStateProjector(new RouteableSchema(new RouteablesOptions { Exposure = RouteableExposure.Public }));
        var told = new List<Type>();
        projector.SentEmpty = told.Add;
        projector.Project(new UnmarkedOrdersViewModel());
        Assert.Empty(told);

        Assert.False(TestSchema.Schema.Shape(typeof(object)).HidesAll);
    }

    [Fact]
    public async Task A_session_in_development_says_when_an_object_is_sent_empty()
    {
        await using var provider = Services();       // DetailedErrors, as in Development
        await using var session = new RouteableSession(provider, new RecordingClient());
        Assert.NotNull(session.Projector.SentEmpty);

        await using var production = Services(o => o.DetailedErrors = false);
        await using var quiet = new RouteableSession(production, new RecordingClient());
        Assert.Null(quiet.Projector.SentEmpty);
    }

    // ---------------------------------------------------------------- B6: two properties of one client name

    [Fact]
    public void Two_properties_that_differ_by_case_are_refused_as_two_commands_of_one_name_are()
    {
        var schema = new RouteableSchema(new RouteablesOptions());
        var refused = Assert.Throws<InvalidOperationException>(() => schema.StateProperties(typeof(CaseTwins)).ToList());
        Assert.Contains("'url'", refused.Message);
    }

    // ---------------------------------------------------------------- B8: delays a timer cannot wait

    [Fact]
    public void Delays_longer_than_a_timer_waits_are_refused_at_registration()
    {
        var problems = new RouteablesOptions
        {
            ReconnectGracePeriod = TimeSpan.FromDays(50),
            CommandGrace = TimeSpan.FromDays(25),
            EssentialsTimeout = TimeSpan.FromDays(30),
            MinResendInterval = TimeSpan.FromSeconds(-1),
        }.Problems();

        Assert.Contains(problems, p => p.StartsWith("ReconnectGracePeriod", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("CommandGrace", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("EssentialsTimeout", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("MinResendInterval", StringComparison.Ordinal));
        Assert.Equal(4, problems.Count);
        Assert.Empty(new RouteablesOptions { ReconnectGracePeriod = RouteablesOptions.MaxDelay, CommandGrace = RouteablesOptions.MaxDelay }.Problems());
    }

    // ---------------------------------------------------------------- B9: an inline call does not run the whole queue

    [Fact]
    public async Task A_call_made_inline_on_an_idle_session_returns_without_running_what_it_queued()
    {
        var dispatcher = new SessionDispatcher(() => { }, _ => { });
        var release = new ManualResetEventSlim();
        var caller = Environment.CurrentManagedThreadId;
        var queuedRanOn = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var watch = Stopwatch.StartNew();
        var call = dispatcher.InvokeAsync(() =>
        {
            // Work this call queues on its session: a continuation, a change it caused.
            dispatcher.Post(_ =>
            {
                release.Wait(TimeSpan.FromSeconds(3));
                queuedRanOn.TrySetResult(Environment.CurrentManagedThreadId);
            }, null);
            return Task.FromResult(42);
        });
        var returnedAfter = watch.Elapsed;
        release.Set();

        Assert.True(call.IsCompleted);                              // the call itself ran inline
        Assert.Equal(42, await call);
        Assert.True(returnedAfter < TimeSpan.FromSeconds(1));       // and did not wait for what it queued
        Assert.NotEqual(caller, await queuedRanOn.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        await dispatcher.IdleAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    // ---------------------------------------------------------------- S2: a client that asks the whole state in a loop

    [Fact]
    public async Task The_full_state_asked_again_in_a_burst_is_sent_once_at_once_and_once_at_the_end()
    {
        await using var provider = Services(o => o.MinResendInterval = TimeSpan.FromMilliseconds(300));
        var client = new RecordingClient();
        await using var session = new RouteableSession(provider, client);
        await session.GoToAsync("/stream", TestContext.Current.CancellationToken);
        Assert.Single(client.Navigated);

        for (var i = 0; i < 20; i++)
            Assert.True((await session.ResyncAsync(RouteableTargets.Page)).Success);
        await session.FlushAsync();

        Assert.Equal(2, client.Navigated.Count);                                   // the first ask, at once
        Assert.True(await TestSchema.EventuallyAsync(() => client.Navigated.Count == 3));   // the rest of the burst, once
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.Equal(3, client.Navigated.Count);
    }

    [Fact]
    public async Task Without_an_interval_every_ask_sends_the_state_again()
    {
        await using var provider = Services(o => o.MinResendInterval = TimeSpan.Zero);
        var client = new RecordingClient();
        await using var session = new RouteableSession(provider, client);
        await session.GoToAsync("/stream", TestContext.Current.CancellationToken);

        for (var i = 0; i < 3; i++)
            await session.ResyncAsync(RouteableTargets.Page);
        await session.FlushAsync();

        Assert.Equal(4, client.Navigated.Count);
    }
}
