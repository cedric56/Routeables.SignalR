using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Routeables.Abstraction;
using Routeables.Abstraction.Bases;
using Routeables.Abstraction.Registrations;
using Routeables.Abstraction.Routing;
using Routeables.Core;
using Routeables.Routing;
using Routeables.Services;
using Routeables.SignalR;

namespace Routeables.Tests.Remote;

public sealed class ShopView;

public sealed class NoteModalView(RemoteInteractions interactions) : RemoteModal<NoteModalViewModel, string>(interactions);

/// <summary>A command with ExecuteAsync, as the AsyncCommand of the samples.</summary>
public sealed class TestCommand(Func<Task> run, Func<bool>? can = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => can?.Invoke() ?? true;
    public Task ExecuteAsync() => run();
    public async void Execute(object? parameter) => await run();
    public void Raise() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

[RouteableState]
public sealed class NoteModalViewModel : ICloseableViewModel<string>
{
    public NoteModalViewModel() => SaveCommand = new TestCommand(() => CloseAsync("saved"));

    public ICommand SaveCommand { get; }
    [RouteableState(Settable = true)] public string Text { get; set; } = string.Empty;
    public string Title => "Note";
    public ObservableCollection<RouteableCommand> Commands { get; } = [];
    public ICommand? CloseCommand => null;
    public event EventHandler<string?>? CloseRequested;
    public Task CloseAsync(string? value) { CloseRequested?.Invoke(this, value); return Task.CompletedTask; }
}

[RouteableState]
public sealed record ShopItem(string Name, ICommand BuyCommand);

[RouteableState]
public sealed class ShopViewModel : INotifyPropertyChanged
{
    private readonly IRouteableDialogService _dialogs;
    private readonly IRouteableNotificationService _notifications;
    private readonly IServiceProvider _provider;
    private readonly TestCommand _checkout;
    private string _result = string.Empty;

    public ShopViewModel(IRouteableDialogService dialogs, IRouteableNotificationService notifications, IServiceProvider provider)
    {
        _dialogs = dialogs;
        _notifications = notifications;
        _provider = provider;
        _checkout = new TestCommand(() => { Bought += 10; return Task.CompletedTask; }, () => Items?.Count > 1);
        Items = [new ShopItem("tea", new TestCommand(() => { Bought++; return Task.CompletedTask; }))];
        CheckoutCommand = _checkout;
        AskCommand = new TestCommand(AskAsync);
        NotifyCommand = new TestCommand(() => { _notifications.Show("Tip", "Hello", RouteableNotificationType.Success, TimeSpan.FromSeconds(5), () => Result = "clicked", () => Result += "closed"); return Task.CompletedTask; });
        ModalCommand = new TestCommand(ModalAsync);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ShopItem> Items { get; }
    public ICommand CheckoutCommand { get; }
    public ICommand AskCommand { get; }
    public ICommand NotifyCommand { get; }
    public ICommand ModalCommand { get; }
    public int Bought { get; private set; }
    [RouteableState(Settable = true)] public string Result { get => _result; set { _result = value; Raise(); } }

    public void AddItem() { Items.Add(new ShopItem("coffee", new TestCommand(() => { Bought++; return Task.CompletedTask; }))); _checkout.Raise(); }

    private async Task AskAsync() => Result = "answer " + await _dialogs.ShowYesNoAsync("Sure?", "Ask");

    private async Task ModalAsync() => Result = "note " + await _provider.ShowModalAsync<NoteModalViewModel, string>();

    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

[RouteableState]
public sealed class MenuViewModel
{
    public string Title => "Menu";
}

/// <summary>A value that is a name, as the RouteTransition of the samples: "Fade" converts to it.</summary>
public sealed class Mode(string name)
{
    public override string ToString() => name;
    public static implicit operator Mode(string name) => new(name);
}

[RouteableState(Settable = true)]
public sealed class ModeViewModel : INotifyPropertyChanged
{
    private Mode _mode = "Fast";
    public event PropertyChangedEventHandler? PropertyChanged;
    public Mode Mode { get => _mode; set { _mode = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Mode))); } }
}

/// <summary>What a browser client sees of the dialogs, the notifications, the modal, the commands of the state and the targets.</summary>
public class RemoteTargetsTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static ServiceProvider Services()
    {
        var services = TestSchema.Services();
        services.RegisterRoute<ShopView, ShopViewModel>(route: "/shop");
        services.RegisterRoute<NoteModalView, NoteModalViewModel>();
        services.AddRegion("side");
        services.AddRouteableTarget<MenuViewModel>("$menu");
        return services.BuildServiceProvider();
    }

    private static async Task<(ServiceProvider Provider, RecordingClient Client, RouteableSession Session, ShopViewModel Shop)> OpenShopAsync()
    {
        var provider = Services();
        var client = new RecordingClient();
        var session = new RouteableSession(provider, client);
        Assert.True((await session.GoToAsync("/shop", TestContext.Current.CancellationToken)).Success);
        return (provider, client, session, (ShopViewModel)session.Current!);
    }

    private static Task<bool> EventuallyAsync(Func<bool> condition) => TestSchema.EventuallyAsync(condition);

    [Fact]
    public void A_command_of_the_state_is_sent_as_a_token_with_its_path()
    {
        var shop = new ShopViewModel(null!, null!, null!);

        var state = TestSchema.Projector.Project(shop);

        var checkout = (Dictionary<string, object?>)state["checkoutCommand"]!;
        Assert.Equal("checkoutCommand", checkout["$command"]);
        Assert.Equal(false, checkout["canExecute"]);
        var item = (Dictionary<string, object?>)((List<object?>)state["items"]!)[0]!;
        var buy = (Dictionary<string, object?>)item["buyCommand"]!;
        Assert.Equal("items.0.buyCommand", buy["$command"]);
        Assert.Equal(true, buy["canExecute"]);
    }

    [Fact]
    public async Task A_command_of_the_state_is_called_by_its_path_when_it_can_run()
    {
        var shop = new ShopViewModel(null!, null!, null!);

        Assert.True((await TestSchema.Invoker.InvokeAsync(shop, "items.0.buyCommand", null)).Success);
        Assert.Equal(1, shop.Bought);

        Assert.False((await TestSchema.Invoker.InvokeAsync(shop, "checkoutCommand", null)).Success);   // disabled
        Assert.False((await TestSchema.Invoker.InvokeAsync(shop, "items.7.buyCommand", null)).Success);
        Assert.False((await TestSchema.Invoker.InvokeAsync(shop, "bought", null)).Success);           // not a command
        Assert.Equal(1, shop.Bought);
    }

    [Fact]
    public async Task A_change_inside_the_state_is_told_for_the_property_that_holds_it()
    {
        var (provider, client, session, shop) = await OpenShopAsync();
        await using var _ = provider;
        await using var __ = session;

        shop.AddItem();

        // The collection and the command whose CanExecute changed: the client gets the new value of both properties.
        Assert.True(await EventuallyAsync(() => client.Changed.Any(c => c.Property == "items")));
        Assert.True(await EventuallyAsync(() => client.Changed.Any(c => c.Property == "checkoutCommand")));
        var checkout = (Dictionary<string, object?>)client.Changed.Last(c => c.Property == "checkoutCommand").Value!;
        Assert.Equal(true, checkout["canExecute"]);

        // And the new item is watched too.
        client.Changed.Clear();
        Assert.True((await session.InvokeAsync("items.1.buyCommand", [])).Success);
        Assert.Equal(1, shop.Bought);
    }

    [Fact]
    public async Task A_dialog_goes_to_the_client_and_the_command_goes_on_with_the_answer()
    {
        var (provider, client, session, shop) = await OpenShopAsync();
        await using var _ = provider;
        await using var __ = session;

        // The command does not end before the visitor answers: the call returns once the dialog is asked.
        Assert.True((await session.InvokeAsync("askCommand", [])).Success);

        var dialog = Assert.Single(client.Dialogs);
        Assert.Equal("Ask", dialog.Title);
        Assert.Equal(2, dialog.Buttons.Count);
        Assert.Equal("Yes", dialog.Buttons[0]);
        Assert.Equal("No", dialog.Buttons[1]);
        Assert.Equal(string.Empty, shop.Result);

        Assert.False((await session.AnswerAsync("nobody", 0)).Success);
        Assert.False((await session.AnswerAsync(dialog.Id, 5)).Success);
        Assert.True((await session.AnswerAsync(dialog.Id, 1)).Success);

        // The value is set, then told: wait for what the client receives.
        Assert.True(await EventuallyAsync(() => client.Changed.Any(c => c.Property == "result" && Equals(c.Value, "answer False"))));
        Assert.Equal("answer False", shop.Result);
    }

    [Fact]
    public async Task A_notification_goes_to_the_client_which_closes_or_clicks_it()
    {
        var (provider, client, session, shop) = await OpenShopAsync();
        await using var _ = provider;
        await using var __ = session;

        Assert.True((await session.InvokeAsync("notifyCommand", [])).Success);

        var toast = Assert.Single(client.Toasts);
        Assert.Equal("Success", toast.Type);
        Assert.Equal(5, toast.ExpirationSeconds);

        Assert.True((await session.DismissAsync(toast.Id, click: true)).Success);
        Assert.Equal("clickedclosed", shop.Result);
        Assert.False((await session.DismissAsync(toast.Id, click: false)).Success);
    }

    [Fact]
    public async Task A_modal_is_a_page_of_the_modal_target_until_it_closes()
    {
        var (provider, client, session, shop) = await OpenShopAsync();
        await using var _ = provider;
        await using var __ = session;

        Assert.True((await session.InvokeAsync("modalCommand", [])).Success);

        var page = Assert.Single(client.Navigated, n => n.Target == RouteableTargets.Modal);
        Assert.Equal(nameof(NoteModalViewModel), page.ViewModel);
        Assert.Equal("Note", page.State["title"]);

        // The modal is edited and closed through its own target.
        Assert.True((await session.SetOnAsync(RouteableTargets.Modal, "text", Json("\"abc\""))).Success);
        Assert.True((await session.InvokeOnAsync(RouteableTargets.Modal, "saveCommand", [])).Success);

        Assert.True(await EventuallyAsync(() => shop.Result == "note saved"));
        Assert.Contains(client.Closed, c => c.Target == RouteableTargets.Modal);
        Assert.False((await session.InvokeOnAsync(RouteableTargets.Modal, "saveCommand", [])).Success);
    }

    [Fact]
    public async Task A_region_and_a_registered_view_model_are_watched_when_the_client_asks()
    {
        await using var provider = Services();
        var client = new RecordingClient();
        await using var session = new RouteableSession(provider, client);

        Assert.True((await session.WatchAsync("$menu")).Success);
        var menu = Assert.Single(client.Navigated, n => n.Target == "$menu");
        Assert.Equal("Menu", menu.State["title"]);

        Assert.True((await session.WatchAsync("side")).Success);
        Assert.DoesNotContain(client.Navigated, n => n.Target == "side");   // nothing shown yet
        Assert.True((await session.GoToInAsync("side", "/shop", TestContext.Current.CancellationToken)).Success);
        var page = Assert.Single(client.Navigated, n => n.Target == "side");
        Assert.Equal(nameof(ShopViewModel), page.ViewModel);

        // The main page is untouched, and the region has its own commands.
        Assert.Null(session.Current);
        Assert.True((await session.InvokeOnAsync("side", "items.0.buyCommand", [])).Success);

        Assert.False((await session.WatchAsync("$unknown")).Success);
        Assert.False((await session.WatchAsync("nowhere")).Success);
        Assert.False((await session.InvokeOnAsync("never-watched", "x", [])).Success);
        Assert.False((await session.GoToInAsync("nowhere", "/shop", TestContext.Current.CancellationToken)).Success);

        Assert.True((await session.UnwatchAsync("side")).Success);
        Assert.False((await session.InvokeOnAsync("side", "items.0.buyCommand", [])).Success);
    }

    [Fact]
    public async Task A_client_can_not_watch_more_than_the_limit_even_when_it_asks_all_at_once()
    {
        const int max = 5;
        var services = TestSchema.Services(o => o.MaxTargets = max);
        for (var i = 0; i < max + 2; i++)
            services.AddRegion("r" + i);
        await using var provider = services.BuildServiceProvider();
        await using var session = new RouteableSession(provider, new RecordingClient());

        // Asked from several threads at once: the check and the add are one step on the dispatcher of the session.
        var results = await Task.WhenAll(Enumerable.Range(0, max + 2).Select(i => Task.Run(() => session.WatchAsync("r" + i), TestContext.Current.CancellationToken)));

        Assert.Equal(max, results.Count(r => r.Success));   // the main page and the modal do not count
        Assert.All(results.Where(r => !r.Success), r => Assert.Equal(RouteableErrorCodes.Limit, r.Code));
    }

    [Fact]
    public async Task A_value_that_is_a_name_is_sent_and_set_as_a_string()
    {
        var viewModel = new ModeViewModel();
        Assert.Equal("Fast", TestSchema.Projector.Project(viewModel)["mode"]);

        var services = TestSchema.Services();
        services.RegisterRoute<ShopView, ModeViewModel>(route: "/mode");
        await using var provider = services.BuildServiceProvider();
        var client = new RecordingClient();
        await using var session = new RouteableSession(provider, client);
        Assert.True((await session.GoToAsync("/mode", TestContext.Current.CancellationToken)).Success);

        Assert.True((await session.SetOnAsync(RouteableTargets.Page, "mode", Json("\"Slow\""))).Success);
        Assert.Equal("Slow", ((ModeViewModel)session.Current!).Mode.ToString());
        Assert.True(await EventuallyAsync(() => client.Changed.Any(c => c.Property == "mode" && Equals(c.Value, "Slow"))));
    }
}
