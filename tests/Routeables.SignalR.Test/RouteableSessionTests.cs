using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using SignalRDialog = Routeables.SignalR.RouteableDialog;
using Routeables.Abstraction;
using Routeables.Core;
using Routeables.SignalR;

namespace Routeables.Tests.Remote;

public sealed class CounterView;

public sealed record CounterItem(string Name, int Quantity);

public sealed class CounterViewModel(IRouter? router = null) : INotifyPropertyChanged
{
    private int _count;
    private string _title = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Count { get => _count; set { _count = value; Raise(); } }
    public string Title { get => _title; set { _title = value; Raise(); } }
    public List<CounterItem> Items { get; } = [];

    // Not state: a service, a command object and an ignored property stay on the server.
    public IRouter? Router => router;
    public System.Windows.Input.ICommand? Command => null;
    [System.Text.Json.Serialization.JsonIgnore] public string Hidden => "hidden";

    [RouteableCommand] public void Increment() => Count++;
    [RouteableCommand] public void Add(int amount) => Count += amount;
    [RouteableCommand] public void Rename(string title = "default") => Title = title;
    [RouteableCommand] public async Task SlowAsync() { await Task.Delay(10); Count = 100; }
    [RouteableCommand] public void Fail() => throw new InvalidOperationException("failed");
    public void NotACommand() { }

    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A list that a test reads while the session writes to it from another thread.</summary>
internal sealed class Told<T> : IReadOnlyList<T>
{
    private readonly List<T> _items = [];

    public void Add(T item) { lock (_items) _items.Add(item); }

    public void Clear() { lock (_items) _items.Clear(); }

    public int Count { get { lock (_items) return _items.Count; } }

    public T this[int index] { get { lock (_items) return _items[index]; } }

    public IEnumerator<T> GetEnumerator() { lock (_items) return ((IEnumerable<T>)[.. _items]).GetEnumerator(); }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

internal sealed class RecordingClient : IRouteableClient
{
    public Told<RouteableNavigated> Navigated { get; } = new();
    public Told<RouteableChanged> Changed { get; } = new();
    public Told<RouteableFailure> Failed { get; } = new();
    public Told<SignalRDialog> Dialogs { get; } = new();
    public Told<RouteableToast> Toasts { get; } = new();
    public Told<RouteableClosed> Closed { get; } = new();

    Task IRouteableClient.Navigated(RouteableNavigated message) { Navigated.Add(message); return Task.CompletedTask; }
    Task IRouteableClient.Changed(RouteableChanged message) { Changed.Add(message); return Task.CompletedTask; }
    Task IRouteableClient.Failed(RouteableFailure message) { Failed.Add(message); return Task.CompletedTask; }
    Task IRouteableClient.DialogRequested(SignalRDialog message) { Dialogs.Add(message); return Task.CompletedTask; }
    Task IRouteableClient.Notified(RouteableToast message) { Toasts.Add(message); return Task.CompletedTask; }
    Task IRouteableClient.Closed(RouteableClosed message) { Closed.Add(message); return Task.CompletedTask; }
}

/// <summary>What a browser client sees of a view model: its state, its commands, and what changes.</summary>
public class RouteableSessionTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void The_state_holds_the_properties_a_client_can_show_and_nothing_else()
    {
        var viewModel = new CounterViewModel { Title = "T" };
        viewModel.Items.Add(new CounterItem("a", 1));

        var state = RouteableStateProjector.Project(viewModel);

        Assert.Equal(0, state["count"]);
        Assert.Equal("T", state["title"]);
        Assert.Single((List<object?>)state["items"]!);
        Assert.DoesNotContain("router", state.Keys);
        Assert.Null(state["command"]);   // a command that is not there is null, not a token
        Assert.DoesNotContain("hidden", state.Keys);
    }

    [Fact]
    public void Only_the_methods_that_say_so_are_commands()
    {
        var commands = RouteableStateProjector.Commands(typeof(CounterViewModel));

        Assert.Contains("increment", commands.Keys);
        Assert.Contains("add", commands.Keys);
        Assert.DoesNotContain("notACommand", commands.Keys);
    }

    [Fact]
    public async Task A_command_is_called_with_the_arguments_of_the_client()
    {
        var viewModel = new CounterViewModel();

        Assert.True((await RouteableCommandInvoker.InvokeAsync(viewModel, "add", [Json("5")])).Success);
        Assert.Equal(5, viewModel.Count);

        Assert.True((await RouteableCommandInvoker.InvokeAsync(viewModel, "rename", null)).Success);
        Assert.Equal("default", viewModel.Title);

        Assert.True((await RouteableCommandInvoker.InvokeAsync(viewModel, "slowAsync", null)).Success);
        Assert.Equal(100, viewModel.Count);
    }

    [Fact]
    public async Task A_call_that_is_not_allowed_or_not_valid_is_refused_and_says_why()
    {
        var viewModel = new CounterViewModel();

        Assert.False((await RouteableCommandInvoker.InvokeAsync(viewModel, "notACommand", null)).Success);
        Assert.False((await RouteableCommandInvoker.InvokeAsync(viewModel, "add", null)).Success);
        Assert.False((await RouteableCommandInvoker.InvokeAsync(viewModel, "add", [Json("\"x\"")])).Success);

        var failed = await RouteableCommandInvoker.InvokeAsync(viewModel, "fail", null);
        Assert.False(failed.Success);
        Assert.Equal("failed", failed.Error);
    }

    private static ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRouteablesSignalR();
        services.RegisterRoute<CounterView, CounterViewModel>(route: "/counter");
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task A_session_tells_the_client_the_page_then_what_changes()
    {
        await using var provider = Services();
        var client = new RecordingClient();
        await using var session = new RouteableSession(provider, client);

        Assert.True((await session.GoToAsync("/counter", TestContext.Current.CancellationToken)).Success);

        var page = Assert.Single(client.Navigated);
        Assert.Equal("/counter", page.Route);
        Assert.Equal(nameof(CounterViewModel), page.ViewModel);
        Assert.Contains("increment", page.Commands);

        Assert.True((await session.InvokeAsync("increment", null)).Success);
        Assert.Contains(client.Changed, c => c.Property == "count" && Equals(c.Value, 1));

        Assert.True(session.Set("title", Json("\"hello\"")).Success);
        Assert.Contains(client.Changed, c => c.Property == "title" && Equals(c.Value, "hello"));

        Assert.False(session.Set("router", Json("1")).Success);
        Assert.False(session.Set("count", Json("\"x\"")).Success);
    }

    [Fact]
    public async Task A_route_that_does_not_exist_fails_and_the_client_is_told()
    {
        await using var provider = Services();
        var client = new RecordingClient();
        await using var session = new RouteableSession(provider, client);

        var ack = await session.GoToAsync("/nowhere", TestContext.Current.CancellationToken);

        Assert.False(ack.Success);
        Assert.Single(client.Failed);
        Assert.Empty(client.Navigated);
        Assert.False((await session.GoToAsync(" ", TestContext.Current.CancellationToken)).Success);
    }

    [Fact]
    public async Task A_closed_session_tells_nothing_more()
    {
        await using var provider = Services();
        var client = new RecordingClient();
        var session = new RouteableSession(provider, client);
        await session.GoToAsync("/counter", TestContext.Current.CancellationToken);
        var viewModel = (CounterViewModel)session.Current!;
        var told = client.Changed.Count;

        await session.DisposeAsync();
        viewModel.Count++;

        Assert.Equal(told, client.Changed.Count);
    }
}
