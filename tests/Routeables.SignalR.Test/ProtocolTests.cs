using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Routeables.Abstraction;
using Routeables.Core;
using Routeables.Services;
using Routeables.SignalR;

namespace Routeables.Tests.Remote;

public sealed class StreamView;

[RouteableState]
public sealed class Line : INotifyPropertyChanged
{
    private string _text;

    public Line(string text) => _text = text;

    public event PropertyChangedEventHandler? PropertyChanged;

    [RouteableState(Settable = true)]
    public string Text { get => _text; set { _text = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text))); } }
}

[RouteableState]
public sealed class StreamViewModel(IRouteableDialogService dialogs) : INotifyPropertyChanged
{
    private int _value;
    private bool _busy;
    private int _inside;

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Value { get => _value; set { _value = value; Raise(); } }
    public bool Busy { get => _busy; set { _busy = value; Raise(); } }
    public ObservableCollection<string> Words { get; } = [];
    public ObservableCollection<Line> Lines { get; } = [];
    private List<int> _numbers = [];
    public List<int> Numbers { get => _numbers; set { _numbers = value; Raise(); } }

    /// <summary>How many commands ran at the same time, at most.</summary>
    public int MostAtOnce { get; private set; }

    [RouteableCommand]
    public void Count(int times)
    {
        for (var i = 1; i <= times; i++)
            Value = i;
    }

    [RouteableCommand]
    public async Task SlowAsync(int milliseconds)
    {
        Enter();
        try
        {
            await Task.Delay(milliseconds);
            Value++;
        }
        finally { _inside--; }
    }

    /// <summary>Not thread safe at all: two threads inside it lose an update.</summary>
    [RouteableCommand]
    public void Critical()
    {
        Enter();
        try
        {
            var read = Value;
            Thread.Sleep(1);
            Value = read + 1;
        }
        finally { _inside--; }
    }

    [RouteableCommand]
    public async Task AskAsync()
    {
        Busy = true;
        var yes = await dialogs.ShowYesNoAsync("Go on?", "Ask");
        Busy = false;
        Value = yes ? 1 : -1;
    }

    private void Enter()
    {
        _inside++;
        MostAtOnce = Math.Max(MostAtOnce, _inside);
    }

    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A page that asks the visitor before it lets him leave.</summary>
[RouteableState]
public sealed class GuardedViewModel(IRouteableDialogService dialogs) : IRouteable
{
    public string Name => "guarded";

    public Task<bool> CanLeaveAsync(CancellationToken ct = default) => dialogs.ShowYesNoAsync("Leave without saving?", "Leave");
}

/// <summary>A client whose messages wait until the test lets them go, as a slow network.</summary>
internal sealed class SlowClient(RecordingClient inner) : IRouteableClient
{
    private TaskCompletionSource _gate = Open();

    public RecordingClient Inner => inner;

    public void Close() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Release() => _gate.TrySetResult();

    private static TaskCompletionSource Open()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.SetResult();
        return gate;
    }

    private async Task Pass(Func<IRouteableClient, Task> send)
    {
        await _gate.Task;
        await send(inner);
    }

    public Task Navigated(RouteableNavigated message) => Pass(c => c.Navigated(message));
    public Task Changed(RouteableChanged message) => Pass(c => c.Changed(message));
    public Task Failed(RouteableFailure message) => Pass(c => c.Failed(message));
    public Task DialogRequested(RouteableDialog message) => Pass(c => c.DialogRequested(message));
    public Task Notified(RouteableToast message) => Pass(c => c.Notified(message));
    public Task Closed(RouteableClosed message) => Pass(c => c.Closed(message));
    public Task Completed(RouteableCompleted message) => Pass(c => c.Completed(message));
}

/// <summary>The order of the messages, their generations and versions, the merging of changes, and the calls that go on.</summary>
public class ProtocolTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static ServiceProvider Services(Action<RouteablesOptions>? options = null)
    {
        var services = TestSchema.Services(options);
        services.RegisterRoute<StreamView, StreamViewModel>(route: "/stream");
        services.RegisterRoute<StreamView, GuardedViewModel>(route: "/guarded");
        services.RegisterRoute<StreamView, CounterViewModel>(route: "/counter");
        return services.BuildServiceProvider();
    }

    private static void AssertContiguous(RecordingClient client, string target = RouteableTargets.Page)
    {
        foreach (var page in client.Batches.Where(b => b.Target == target).GroupBy(b => b.Generation))
            Assert.Equal(Enumerable.Range(1, page.Count()).Select(v => (long)v), page.Select(b => b.Version));
    }

    [Fact]
    public async Task Every_page_is_a_new_generation_and_its_changes_are_numbered_from_one()
    {
        await using var provider = Services();
        var client = new RecordingClient();
        await using var session = new RouteableSession(provider, client);

        await session.GoToAsync("/stream", TestContext.Current.CancellationToken);
        await session.InvokeAsync("count", [Json("1")]);
        await session.InvokeAsync("count", [Json("2")]);
        await session.GoToAsync("/guarded", TestContext.Current.CancellationToken);

        Assert.Equal(new long[] { 1, 2 }, client.Navigated.Select(n => n.Generation).ToArray());
        Assert.All(client.Batches, b => Assert.Equal(1, b.Generation));
        AssertContiguous(client);
    }

    [Fact]
    public async Task What_changes_during_one_turn_is_sent_once_with_its_last_value()
    {
        await using var provider = Services();
        var client = new RecordingClient();
        await using var session = new RouteableSession(provider, client);
        await session.GoToAsync("/stream", TestContext.Current.CancellationToken);

        Assert.True((await session.InvokeAsync("count", [Json("1000")])).Success);

        var change = Assert.Single(client.Changed);
        Assert.Equal("value", change.Property);
        Assert.Equal(1000, change.Value);
    }

    [Fact]
    public async Task The_answer_to_a_call_comes_after_the_state_it_changed_even_for_a_slow_client()
    {
        await using var provider = Services();
        var slow = new SlowClient(new RecordingClient());
        await using var session = new RouteableSession(provider, slow);
        await session.GoToAsync("/stream", TestContext.Current.CancellationToken);

        slow.Close();
        var call = session.InvokeAsync("count", [Json("3")]);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(call.IsCompleted);   // the change is not written yet

        slow.Release();
        Assert.True((await call).Success);
        Assert.Contains(slow.Inner.Changed, c => c.Property == "value" && Equals(c.Value, 3));
    }

    [Fact]
    public async Task Changes_that_wait_for_a_slow_client_are_merged_and_stay_numbered_without_holes()
    {
        await using var provider = Services();
        var slow = new SlowClient(new RecordingClient());
        await using var session = new RouteableSession(provider, slow);
        await session.GoToAsync("/stream", TestContext.Current.CancellationToken);
        var viewModel = (StreamViewModel)session.Current!;

        slow.Close();
        for (var i = 1; i <= 500; i++)
            viewModel.Value = i;   // each from the test thread: one turn each
        await session.Dispatcher.IdleAsync();   // every change was told; the messages wait for the client
        slow.Release();
        await session.FlushAsync();

        Assert.True(slow.Inner.Batches.Count < 10, $"{slow.Inner.Batches.Count} messages for 500 changes");
        Assert.Equal(500, slow.Inner.Changed.Last(c => c.Property == "value").Value);
        AssertContiguous(slow.Inner);
    }

    [Fact]
    public async Task A_change_of_a_page_that_was_left_is_never_applied_to_the_next_one()
    {
        await using var provider = Services();
        var slow = new SlowClient(new RecordingClient());
        await using var session = new RouteableSession(provider, slow);
        await session.GoToAsync("/stream", TestContext.Current.CancellationToken);
        var first = (StreamViewModel)session.Current!;

        slow.Close();
        first.Value = 41;                          // queued for the first page
        var navigation = session.GoToAsync("/stream", TestContext.Current.CancellationToken);   // a new page of the same type: same property names
        slow.Release();
        await navigation;
        first.Value = 42;                          // the old page changes again: it is no longer followed

        await session.FlushAsync();
        var last = slow.Inner.Navigated.Last();
        Assert.Equal(2, last.Generation);
        Assert.DoesNotContain(slow.Inner.Changed, c => c.Generation == last.Generation);
        Assert.Equal(0, last.State["value"]);
    }

    [Fact]
    public async Task Changes_made_on_other_threads_arrive_in_order_on_the_session()
    {
        await using var provider = Services();
        var client = new RecordingClient();
        await using var session = new RouteableSession(provider, client);
        await session.GoToAsync("/stream", TestContext.Current.CancellationToken);
        var viewModel = (StreamViewModel)session.Current!;

        await Task.WhenAll(Enumerable.Range(0, 4).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 250; i++)
                viewModel.Value = i;
        }, TestContext.Current.CancellationToken)));
        await session.FlushAsync();

        AssertContiguous(client);
        Assert.Equal(viewModel.Value, client.Changed.Last(c => c.Property == "value").Value);
    }

    [Fact]
    public async Task Commands_of_one_session_never_run_on_two_threads_at_once()
    {
        await using var provider = Services();
        await using var session = new RouteableSession(provider, new RecordingClient());
        await session.GoToAsync("/stream", TestContext.Current.CancellationToken);
        var viewModel = (StreamViewModel)session.Current!;

        // Fifty calls at once, from fifty threads, as fifty hub invocations of a client that does not wait.
        var acks = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => session.InvokeAsync("critical", []), TestContext.Current.CancellationToken)));

        Assert.All(acks, a => Assert.True(a.Success));
        Assert.Equal(1, viewModel.MostAtOnce);
        Assert.Equal(50, viewModel.Value);   // no update lost
    }

    [Fact]
    public async Task A_long_call_is_answered_as_pending_then_completed_after_the_state_it_changed()
    {
        await using var provider = Services(o => o.CommandGrace = TimeSpan.FromMilliseconds(30));
        var client = new RecordingClient();
        await using var session = new RouteableSession(provider, client);
        await session.GoToAsync("/stream", TestContext.Current.CancellationToken);

        var ack = await session.InvokeAsync("slowAsync", [Json("200")]);

        Assert.True(ack.Success);
        Assert.True(ack.Pending);
        Assert.True(await TestSchema.EventuallyAsync(() => client.Completed.Any(c => c.Operation == ack.Operation)));
        var all = client.All.ToList();
        var completed = all.FindIndex(m => m is RouteableCompleted c && c.Operation == ack.Operation);
        var changed = all.FindIndex(m => m is RouteableChanged);
        Assert.True(changed >= 0 && changed < completed, "the change comes before the end of the call");
        Assert.True(((RouteableCompleted)all[completed]).Success);
    }

    [Fact]
    public async Task Navigating_quickly_while_commands_run_keeps_the_last_page_and_ends_every_call()
    {
        await using var provider = Services(o => o.CommandGrace = TimeSpan.FromMilliseconds(20));
        var client = new RecordingClient();
        await using var session = new RouteableSession(provider, client);
        await session.GoToAsync("/stream", TestContext.Current.CancellationToken);

        var acks = new List<RouteableAck>();
        for (var i = 0; i < 5; i++)
        {
            acks.Add(await session.InvokeAsync("slowAsync", [Json("150")]));
            acks.Add(await session.GoToAsync(i % 2 == 0 ? "/counter" : "/stream", TestContext.Current.CancellationToken));
        }
        await session.GoToAsync("/stream", TestContext.Current.CancellationToken);
        Assert.True(await TestSchema.EventuallyAsync(() => acks.Where(a => a.Pending).All(a => client.Completed.Any(c => c.Operation == a.Operation))));
        await session.FlushAsync();

        var last = client.Navigated.Last(n => n.Target == RouteableTargets.Page);
        Assert.Equal(nameof(StreamViewModel), last.ViewModel);
        Assert.IsType<StreamViewModel>(session.Current);
        Assert.DoesNotContain(client.Batches, b => b.Generation > last.Generation);
        Assert.All(client.Batches.Where(b => b.Generation == last.Generation), b => Assert.Equal(b.Generation, last.Generation));
        AssertContiguous(client);
    }

    [Fact]
    public async Task A_dialog_comes_after_the_changes_made_before_it_was_asked()
    {
        await using var provider = Services();
        var client = new RecordingClient();
        await using var session = new RouteableSession(provider, client);
        await session.GoToAsync("/stream", TestContext.Current.CancellationToken);

        var ack = await session.InvokeAsync("askAsync", []);
        Assert.True(ack.Pending);

        var all = client.All.ToList();
        var busy = all.FindIndex(m => m is RouteableChanged c && c.Patches.Any(p => Equals(p.Path[0], "busy") && Equals(p.Value, true)));
        var dialog = all.FindIndex(m => m is RouteableDialog);
        Assert.True(busy >= 0 && busy < dialog);

        Assert.True((await session.AnswerAsync(client.Dialogs[0].Id, 0)).Success);
        Assert.True(await TestSchema.EventuallyAsync(() => client.Completed.Any(c => c.Operation == ack.Operation && c.Success)));
        Assert.Equal(1, ((StreamViewModel)session.Current!).Value);
    }

    [Fact]
    public async Task A_route_change_that_asks_the_visitor_does_not_block_the_answer()
    {
        await using var provider = Services();
        var client = new RecordingClient();
        await using var session = new RouteableSession(provider, client);
        await session.GoToAsync("/guarded", TestContext.Current.CancellationToken);

        // The page asks before it lets the visitor leave: the call is answered as soon as it asks.
        var ack = await session.GoToAsync("/stream", TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(ack.Pending);
        var dialog = Assert.Single(client.Dialogs);
        Assert.Equal("Leave", dialog.Title);

        Assert.True((await session.AnswerAsync(dialog.Id, 0)).Success);   // Yes
        Assert.True(await TestSchema.EventuallyAsync(() => client.Completed.Any(c => c.Operation == ack.Operation && c.Success)));
        Assert.IsType<StreamViewModel>(session.Current);
    }

    [Fact]
    public async Task A_list_tells_what_was_added_removed_moved_or_replaced()
    {
        await using var provider = Services();
        var client = new RecordingClient();
        await using var session = new RouteableSession(provider, client);
        await session.GoToAsync("/stream", TestContext.Current.CancellationToken);
        var viewModel = (StreamViewModel)session.Current!;

        await session.Dispatcher.InvokeAsync(() =>
        {
            viewModel.Words.Add("a");
            viewModel.Words.Add("b");
            viewModel.Words.Add("c");
        });
        await session.Dispatcher.InvokeAsync(() => viewModel.Words.Move(0, 2));
        await session.Dispatcher.InvokeAsync(() => viewModel.Words[1] = "z");
        await session.Dispatcher.InvokeAsync(() => viewModel.Words.RemoveAt(0));
        await session.FlushAsync();

        var splices = client.Changed.Where(c => c.Property == "words").ToList();
        Assert.All(splices, c => Assert.Equal(RouteablePatch.Splice, c.Op));
        Assert.Equal(new List<object?> { "z", "a" }, Replay([], splices));
        Assert.Equal(viewModel.Words.Cast<object?>().ToList(), Replay([], splices));
    }

    [Fact]
    public async Task A_change_inside_an_item_sends_that_item_only()
    {
        await using var provider = Services();
        var client = new RecordingClient();
        await using var session = new RouteableSession(provider, client);
        await session.GoToAsync("/stream", TestContext.Current.CancellationToken);
        var viewModel = (StreamViewModel)session.Current!;
        await session.Dispatcher.InvokeAsync(() => { viewModel.Lines.Add(new Line("one")); viewModel.Lines.Add(new Line("two")); });

        await session.Dispatcher.InvokeAsync(() => viewModel.Lines[1].Text = "deux");
        await session.FlushAsync();

        var change = client.Changed.Last();
        Assert.Equal("lines.1", change.Property);
        Assert.Equal("deux", ((Dictionary<string, object?>)change.Value!)["text"]);
    }

    [Fact]
    public async Task A_collection_too_large_is_not_sent_and_the_client_is_told_why()
    {
        await using var provider = Services(o => o.MaxCollectionItems = 100);
        var client = new RecordingClient();
        await using var session = new RouteableSession(provider, client);
        await session.GoToAsync("/stream", TestContext.Current.CancellationToken);
        var viewModel = (StreamViewModel)session.Current!;

        await session.Dispatcher.InvokeAsync(() =>
        {
            viewModel.Numbers = [.. Enumerable.Range(0, 100_000)];
            viewModel.Value = 1;   // and the other properties go on
        });
        await session.FlushAsync();

        Assert.Contains(client.Changed, c => c.Property == "numbers" && c.Value is null);
        Assert.Contains(client.Failed, f => f.Code == RouteableErrorCodes.Limit && f.Message.Contains("numbers", StringComparison.Ordinal));
        Assert.Contains(client.Changed, c => c.Property == "value" && Equals(c.Value, 1));
    }

    [Fact]
    public async Task A_client_too_slow_to_follow_is_disconnected_rather_than_buffered_without_end()
    {
        var aborted = new TaskCompletionSource();
        await using var provider = Services(o => o.MaxPendingMessages = 5);
        var slow = new SlowClient(new RecordingClient());
        await using var session = new RouteableSession(provider, slow, abort: () => aborted.TrySetResult());
        await session.GoToAsync("/stream", TestContext.Current.CancellationToken);
        var notifications = session.Services.GetRequiredService<IRouteableNotificationService>();

        slow.Close();
        for (var i = 0; i < 20; i++)
            notifications.Show("n" + i, "message");   // notifications are not merged

        await aborted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        slow.Release();
    }

    [Fact]
    public async Task A_target_sends_its_state_again_when_the_client_asks_and_it_is_the_same_page()
    {
        await using var provider = Services();
        var client = new RecordingClient();
        await using var session = new RouteableSession(provider, client);
        await session.GoToAsync("/stream", TestContext.Current.CancellationToken);
        await session.InvokeAsync("count", [Json("4")]);

        Assert.True((await session.ResyncAsync(RouteableTargets.Page)).Success);

        Assert.Equal(2, client.Navigated.Count);
        Assert.Equal(client.Navigated[0].Instance, client.Navigated[1].Instance);
        Assert.True(client.Navigated[1].Generation > client.Navigated[0].Generation);
        Assert.Equal(4, client.Navigated[1].State["value"]);
        Assert.Equal(RouteableErrorCodes.NotFound, (await session.ResyncAsync("nowhere")).Code);
    }

    private static List<object?> Replay(List<object?> list, IEnumerable<ChangeSeen> splices)
    {
        foreach (var splice in splices)
        {
            list.RemoveRange(splice.Index!.Value, splice.Remove!.Value);
            list.InsertRange(splice.Index.Value, (IEnumerable<object?>)splice.Value!);
        }
        return list;
    }
}
