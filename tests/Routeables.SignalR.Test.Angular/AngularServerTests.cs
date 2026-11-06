using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Routeables.SignalR;
using XAnimals.AngularServer;
using XAnimals.ViewModels;

namespace Routeables.Tests.Angular;

/// <summary>A list that a test reads while the session writes to it from another thread.</summary>
internal sealed class Told<T> : IReadOnlyList<T>
{
    private readonly List<T> _items = [];

    /// <summary>Adds an item under a lock so concurrent writers are safe.</summary>
    public void Add(T item) { lock (_items) _items.Add(item); }

    /// <summary>Number of items currently stored.</summary>
    public int Count { get { lock (_items) return _items.Count; } }

    /// <summary>Item at the given index.</summary>
    public T this[int index] { get { lock (_items) return _items[index]; } }

    /// <summary>Snapshot enumerator of the current contents.</summary>
    public IEnumerator<T> GetEnumerator() { lock (_items) return ((IEnumerable<T>)[.. _items]).GetEnumerator(); }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>What the Angular app receives, in the order it receives it.</summary>
internal sealed class AppClient : IRouteableClient
{
    /// <summary>Pages that became current.</summary>
    public Told<RouteableNavigated> Navigated { get; } = new();

    /// <summary>Property changes that arrived after a page was shown.</summary>
    public Told<RouteableChanged> Changed { get; } = new();

    /// <summary>Failed navigations.</summary>
    public Told<RouteableFailure> Failed { get; } = new();

    /// <summary>Dialogs the server requested.</summary>
    public Told<RouteableDialog> Dialogs { get; } = new();

    /// <summary>Toasts the server sent.</summary>
    public Told<RouteableToast> Toasts { get; } = new();

    /// <summary>Targets that were closed (for example the modal).</summary>
    public Told<RouteableClosed> Closed { get; } = new();

    Task IRouteableClient.Navigated(RouteableNavigated message) { Navigated.Add(message); return Task.CompletedTask; }
    Task IRouteableClient.Changed(RouteableChanged message) { Changed.Add(message); return Task.CompletedTask; }
    Task IRouteableClient.Failed(RouteableFailure message) { Failed.Add(message); return Task.CompletedTask; }
    Task IRouteableClient.DialogRequested(RouteableDialog message) { Dialogs.Add(message); return Task.CompletedTask; }
    Task IRouteableClient.Notified(RouteableToast message) { Toasts.Add(message); return Task.CompletedTask; }
    Task IRouteableClient.Closed(RouteableClosed message) { Closed.Add(message); return Task.CompletedTask; }

    /// <summary>The state of a target as the app holds it: the page that arrived, with every change since applied.</summary>
    public Dictionary<string, object?> State(string target)
    {
        var page = Navigated.LastOrDefault(n => n.Target == target)
            ?? throw new InvalidOperationException($"Nothing was shown in '{target}'.");
        var state = new Dictionary<string, object?>(page.State);
        var after = Navigated.Count(n => n.Target == target);
        foreach (var change in Changed.Where(c => c.Target == target))
            state[change.Property] = change.Value;
        _ = after;
        return state;
    }

    /// <summary>View-model type name of the page currently shown in the given target.</summary>
    public string ViewModelOf(string target) => Navigated.Last(n => n.Target == target).ViewModel;
}

/// <summary>One connection of the app: its session, and what it received.</summary>
internal sealed class Connection : IAsyncDisposable
{
    private static readonly JsonElement NoValue = JsonDocument.Parse("null").RootElement;

    public Connection(ServiceProvider provider)
    {
        Client = new AppClient();
        Session = new RouteableSession(provider, Client);
    }

    /// <summary>What the client side of this connection has received.</summary>
    public AppClient Client { get; }

    /// <summary>The server-side session that drives the client.</summary>
    public RouteableSession Session { get; }

    /// <summary>Navigates the main router to the given route.</summary>
    public async Task<RouteableAck> GoToAsync(string route) => await Session.GoToAsync(route);

    /// <summary>Calls the command found in the state of a target, as the app does: it gives the path back.</summary>
    public async Task<RouteableAck> RunAsync(string target, string property)
    {
        var token = Client.State(target)[property] as Dictionary<string, object?>
            ?? throw new InvalidOperationException($"'{property}' is not a command in '{target}'.");
        return await Session.InvokeOnAsync(target, (string)token["$command"]!, []);
    }

    /// <summary>Returns the command token stored under the given property of a target.</summary>
    public Dictionary<string, object?> Token(string target, string property) => (Dictionary<string, object?>)Client.State(target)[property]!;

    /// <summary>Sets a property on a target (serialized as JSON).</summary>
    public RouteableAck Set(string target, string property, object? value)
        => Session.SetOn(target, property, JsonSerializer.SerializeToElement(value ?? NoValue));

    /// <summary>Disposes the underlying session.</summary>
    public ValueTask DisposeAsync() => Session.DisposeAsync();
}

/// <summary>The server of the Angular sample, seen as the app sees it.</summary>
public class AngularServerTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;

    /// <summary>Builds the DI container used by every test.</summary>
    public ValueTask InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRouteablesSignalR<MainViewModel>(s => s.AddAnimalsForAngular());
        _provider = services.BuildServiceProvider();
        return ValueTask.CompletedTask;
    }

    /// <summary>Disposes the shared service provider.</summary>
    public async ValueTask DisposeAsync() => await _provider.DisposeAsync();

    /// <summary>Polls until the condition becomes true or a short timeout elapses.</summary>
    private static async Task<bool> EventuallyAsync(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        return condition();
    }

    /// <summary>Menu entries currently held by the shell.</summary>
    private static List<Dictionary<string, object?>> Entries(AppClient client)
        => [.. ((List<object?>)client.State("$shell")["entries"]!).Cast<Dictionary<string, object?>>()];

    // ---------------------------------------------------------------- the menu

    /// <summary>The shell sends a menu that contains glyphs and omits pages this sample does not serve.</summary>
    [Fact]
    public async Task The_shell_sends_the_menu_with_a_glyph_and_without_the_pages_this_sample_does_not_serve()
    {
        await using var connection = new Connection(_provider);
        Assert.True(connection.Session.Watch("$shell").Success);
        var entries = Entries(connection.Client);
        Assert.Equal(13, entries.Count);
        Assert.Equal("Cats", entries[0]["name"]);
        Assert.Equal("/cats", entries[0]["route"]);
        Assert.All(entries, e => Assert.NotEqual(string.Empty, (string)e["glyph"]!));
        Assert.All(entries, e => Assert.Equal(false, e["isLocked"]));
        Assert.DoesNotContain(entries, e => (string)e["route"]! == "/birds");
    }

    /// <summary>Every menu entry navigates successfully to a page.</summary>
    [Fact]
    public async Task Every_entry_of_the_menu_opens_a_page()
    {
        await using var connection = new Connection(_provider);
        connection.Session.Watch("$shell");
        foreach (var entry in Entries(connection.Client))
        {
            var route = (string)entry["route"]!;
            Assert.True((await connection.GoToAsync(route)).Success, route);
            Assert.NotNull(connection.Session.Current);
        }
        Assert.Empty(connection.Client.Failed);
    }

    /// <summary>An unknown route fails and leaves the current page unchanged.</summary>
    [Fact]
    public async Task A_route_that_does_not_exist_fails_and_leaves_the_page_where_it_was()
    {
        await using var connection = new Connection(_provider);
        await connection.GoToAsync("/cats");
        var page = connection.Session.Current;
        Assert.False((await connection.GoToAsync("/nowhere")).Success);
        Assert.Same(page, connection.Session.Current);
        Assert.Single(connection.Client.Failed);
    }

    /// <summary>Each animal species exposes a searchable list and a detail page.</summary>
    [Fact]
    public async Task Each_species_has_a_list_with_a_search_and_a_detail()
    {
        await using var connection = new Connection(_provider);
        foreach (var species in new[] { "cats", "dogs", "monkeys", "elephants", "bears", "pandas" })
        {
            Assert.True((await connection.GoToAsync("/" + species)).Success, species);
            var animals = (List<object?>)connection.Client.State("")["filteredAnimals"]!;
            Assert.NotEmpty(animals);
            var first = (Dictionary<string, object?>)animals[0]!;
            Assert.True((await connection.GoToAsync($"/{species}/{Uri.EscapeDataString((string)first["name"]!)}")).Success, species + " detail");
            Assert.Contains("Detail", connection.Client.ViewModelOf(""), StringComparison.Ordinal);
            Assert.NotNull(connection.Client.State("")["animal"]);
        }
    }

    /// <summary>Setting search text on a list filters the animals on the server.</summary>
    [Fact]
    public async Task The_search_of_a_list_is_set_by_the_app_and_filters_on_the_server()
    {
        await using var connection = new Connection(_provider);
        await connection.GoToAsync("/bears");
        var all = ((List<object?>)connection.Client.State("")["filteredAnimals"]!).Count;
        Assert.True(connection.Set("", "searchText", "polar").Success);
        Assert.True(await EventuallyAsync(() => ((List<object?>)connection.Client.State("")["filteredAnimals"]!).Count < all));
        Assert.True(((List<object?>)connection.Client.State("")["filteredAnimals"]!).Count >= 1);
    }

    /// <summary>The About page can navigate to polar bears via a command.</summary>
    [Fact]
    public async Task The_about_page_opens_the_polar_bears_by_a_command()
    {
        await using var connection = new Connection(_provider);
        await connection.GoToAsync("/about");
        Assert.True((await connection.RunAsync("", "polarBearsCommand")).Success);
        Assert.True(await EventuallyAsync(() => connection.Client.ViewModelOf("") == nameof(BearsViewModel)));
    }

    // ---------------------------------------------------------------- the theme

    /// <summary>The theme command cycles Default → Light → Dark → Default.</summary>
    [Fact]
    public async Task The_theme_command_goes_round_default_light_dark()
    {
        await using var connection = new Connection(_provider);
        connection.Session.Watch("$shell");
        Assert.Equal("Default", connection.Client.State("$shell")["theme"]);
        foreach (var expected in new[] { "Light", "Dark", "Default" })
        {
            Assert.True((await connection.RunAsync("$shell", "themeCommand")).Success);
            Assert.True(await EventuallyAsync(() => Equals(connection.Client.State("$shell")["theme"], expected)), expected);
        }
    }

    /// <summary>Theme changes on one connection do not affect another connection.</summary>
    [Fact]
    public async Task The_theme_of_a_connection_is_not_the_theme_of_another()
    {
        await using var first = new Connection(_provider);
        await using var second = new Connection(_provider);
        first.Session.Watch("$shell");
        second.Session.Watch("$shell");
        await first.RunAsync("$shell", "themeCommand");
        Assert.True(await EventuallyAsync(() => Equals(first.Client.State("$shell")["theme"], "Light")));
        Assert.Equal("Default", second.Client.State("$shell")["theme"]);
    }

    // ---------------------------------------------------------------- dialogs, notifications, modal

    /// <summary>A message dialog is sent to the client and the answer is delivered back to the view-model.</summary>
    [Fact]
    public async Task A_message_dialog_goes_to_the_app_and_the_answer_goes_back_to_the_view_model()
    {
        await using var connection = new Connection(_provider);
        await connection.GoToAsync("/dialogs");
        Assert.True((await connection.RunAsync("", "yesNoCommand")).Success);
        var dialog = Assert.Single(connection.Client.Dialogs);
        Assert.Equal("message", dialog.Kind);
        Assert.Equal(2, dialog.Buttons.Count);
        Assert.Equal("Yes", dialog.Buttons[0]);
        Assert.True(connection.Session.Answer(dialog.Id, 0).Success);
        // The properties of a page arrive one by one: the result can come before hasResult.
        Assert.True(await EventuallyAsync(() => Equals(connection.Client.State("")["result"], "Answer: Yes.")
            && Equals(connection.Client.State("")["hasResult"], true)));
    }

    /// <summary>Every dialog command on the dialogs page can be opened and answered.</summary>
    [Fact]
    public async Task Every_dialog_of_the_page_can_be_opened_and_answered()
    {
        await using var connection = new Connection(_provider);
        await connection.GoToAsync("/dialogs");
        var commands = new[]
        {
            "okCommand", "errorCommand", "yesNoCommand", "okCancelCommand", "okAbortCommand", "yesNoCancelCommand",
            "yesNoAbortCommand", "contentDialogCommand", "taskDialogCommand", "progressDialogCommand"
        };
        for (var i = 0; i < commands.Length; i++)
        {
            Assert.True((await connection.RunAsync("", commands[i])).Success, commands[i]);
            Assert.Equal(i + 1, connection.Client.Dialogs.Count);
            Assert.True(connection.Session.Answer(connection.Client.Dialogs[i].Id, 0).Success, commands[i]);
            Assert.True(await EventuallyAsync(() => Equals(connection.Client.State("")["hasResult"], true)), commands[i]);
        }
        Assert.Contains(connection.Client.Dialogs, d => d.Kind == "content");
        Assert.Contains(connection.Client.Dialogs, d => d.Kind == "task" && d.ShowProgress);
    }

    /// <summary>A notification reaches the client and a click is reported back to the view-model.</summary>
    [Fact]
    public async Task A_notification_goes_to_the_app_and_its_click_reaches_the_view_model()
    {
        await using var connection = new Connection(_provider);
        await connection.GoToAsync("/dialogs");
        Assert.True((await connection.RunAsync("", "warningCommand")).Success);
        var toast = Assert.Single(connection.Client.Toasts);
        Assert.Equal("Warning", toast.Type);
        Assert.Equal(6, toast.ExpirationSeconds);
        Assert.True(connection.Session.Dismiss(toast.Id, click: true).Success);
        Assert.True(await EventuallyAsync(() => ((string)connection.Client.State("")["result"]!).Contains("notification", StringComparison.Ordinal)));
    }

    /// <summary>The note modal is hosted on the modal target and returns the typed note on save.</summary>
    [Fact]
    public async Task The_note_modal_is_a_page_of_the_modal_target_and_returns_the_note()
    {
        await using var connection = new Connection(_provider);
        await connection.GoToAsync("/dialogs");
        Assert.True((await connection.RunAsync("", "modalCommand")).Success);
        Assert.Equal(nameof(NoteViewModel), connection.Client.ViewModelOf(RouteableTargets.Modal));
        var commands = (List<object?>)connection.Client.State(RouteableTargets.Modal)["commands"]!;
        Assert.Equal(2, commands.Count);
        // Save is disabled while the note is empty, and enabled once it is written.
        var save = (Dictionary<string, object?>)((Dictionary<string, object?>)commands[0]!)["command"]!;
        Assert.Equal(false, save["canExecute"]);
        Assert.False((await connection.Session.InvokeOnAsync(RouteableTargets.Modal, (string)save["$command"]!, [])).Success);
        Assert.True(connection.Set(RouteableTargets.Modal, "note", "Hello Angular").Success);
        Assert.True(await EventuallyAsync(() => Equals(connection.Client.State(RouteableTargets.Modal)["note"], "Hello Angular")));
        Assert.True(await EventuallyAsync(() =>
        {
            var current = (List<object?>)connection.Client.State(RouteableTargets.Modal)["commands"]!;
            var token = (Dictionary<string, object?>)((Dictionary<string, object?>)current[0]!)["command"]!;
            return Equals(token["canExecute"], true);
        }));
        Assert.True((await connection.Session.InvokeOnAsync(RouteableTargets.Modal, (string)save["$command"]!, [])).Success);
        Assert.True(await EventuallyAsync(() => connection.Client.Closed.Any(c => c.Target == RouteableTargets.Modal)));
        Assert.True(await EventuallyAsync(() => Equals(connection.Client.State("")["result"], "Note saved: Hello Angular")));
    }

    /// <summary>Cancelling the note modal closes it without producing a note.</summary>
    [Fact]
    public async Task Cancelling_the_note_modal_closes_it_without_a_note()
    {
        await using var connection = new Connection(_provider);
        await connection.GoToAsync("/dialogs");
        await connection.RunAsync("", "modalCommand");
        var commands = (List<object?>)connection.Client.State(RouteableTargets.Modal)["commands"]!;
        var cancel = (Dictionary<string, object?>)((Dictionary<string, object?>)commands[1]!)["command"]!;
        Assert.True((await connection.Session.InvokeOnAsync(RouteableTargets.Modal, (string)cancel["$command"]!, [])).Success);
        Assert.True(await EventuallyAsync(() => Equals(connection.Client.State("")["result"], "Note cancelled.")));
    }

    // ---------------------------------------------------------------- regions

    /// <summary>The history page can push, go back and go forward inside its region.</summary>
    [Fact]
    public async Task The_history_page_pushes_pages_into_its_region_and_goes_back_and_forward()
    {
        await using var connection = new Connection(_provider);
        connection.Session.Watch("history");
        await connection.GoToAsync("/history");
        Assert.True(await EventuallyAsync(() => connection.Client.Navigated.Any(n => n.Target == "history")));
        var entries = () => ((List<object?>)connection.Client.State("")["entries"]!).Count;
        Assert.Equal(1, entries());
        Assert.True((await connection.RunAsync("", "pushCommand")).Success);
        Assert.True(await EventuallyAsync(() => entries() == 2));
        Assert.True(await EventuallyAsync(() => Equals(connection.Client.State("")["canGoBack"], true)));
        Assert.True((await connection.RunAsync("", "backCommand")).Success);
        Assert.True(await EventuallyAsync(() => Equals(connection.Client.State("")["canGoForward"], true)));
        Assert.True((await connection.RunAsync("", "forwardCommand")).Success);
        Assert.True(await EventuallyAsync(() => Equals(connection.Client.State("")["canGoForward"], false)));
    }

    /// <summary>The transition name of the history frame can be set from the client.</summary>
    [Fact]
    public async Task The_transition_of_the_history_frame_is_set_with_its_name()
    {
        await using var connection = new Connection(_provider);
        await connection.GoToAsync("/history");
        Assert.True(connection.Set("", "transition", "Fade").Success);
        Assert.True(await EventuallyAsync(() => Equals(connection.Client.State("")["transition"], "Fade")));
        Assert.Contains("Fade", ((List<object?>)connection.Client.State("")["transitions"]!).Cast<string>());
    }

    /// <summary>A page that blocks leaving refuses navigation and keeps the current view-model.</summary>
    [Fact]
    public async Task A_page_that_cannot_be_left_refuses_and_the_app_is_told()
    {
        await using var connection = new Connection(_provider);
        connection.Session.Watch("history");
        await connection.GoToAsync("/history");
        Assert.True(connection.Set("", "blockLeaving", true).Success);
        Assert.True(await EventuallyAsync(() => Equals(connection.Client.State("")["blockLeaving"], true)));
        var ack = await connection.GoToAsync("/cats");
        Assert.False(ack.Success);
        Assert.Equal(nameof(HistoryViewModel), connection.Client.ViewModelOf(""));
    }

    /// <summary>The regions page fills four regions; the left-panel menu drives the content region.</summary>
    [Fact]
    public async Task The_regions_page_fills_its_four_regions_and_the_menu_of_the_left_panel_drives_the_content()
    {
        await using var connection = new Connection(_provider);
        var regions = new[] { LayoutController.Header, LayoutController.Footer, LayoutController.LeftPanel, LayoutController.Content };
        foreach (var region in regions)
            Assert.True(connection.Session.Watch(region).Success, region);
        await connection.GoToAsync("/regions");
        foreach (var region in regions)
            Assert.True(await EventuallyAsync(() => connection.Client.Navigated.Any(n => n.Target == region)), region);
        Assert.Equal(nameof(LayoutMenuViewModel), connection.Client.ViewModelOf(LayoutController.LeftPanel));
        Assert.Equal(nameof(CatsViewModel), connection.Client.ViewModelOf(LayoutController.Content));
        var items = (List<object?>)connection.Client.State(LayoutController.LeftPanel)["items"]!;
        var dogs = (Dictionary<string, object?>)((Dictionary<string, object?>)items[1]!)["select"]!;
        Assert.True((await connection.Session.InvokeOnAsync(LayoutController.LeftPanel, (string)dogs["$command"]!, [])).Success);
        Assert.True(await EventuallyAsync(() => connection.Client.ViewModelOf(LayoutController.Content) == nameof(DogsViewModel)));
    }

    /// <summary>Credits temporarily replace the four regions and can restore them.</summary>
    [Fact]
    public async Task The_credits_replace_the_four_regions_and_bring_them_back()
    {
        await using var connection = new Connection(_provider);
        foreach (var region in new[] { LayoutController.Header, LayoutController.Footer, LayoutController.LeftPanel, LayoutController.Content })
            connection.Session.Watch(region);
        await connection.GoToAsync("/regions");
        Assert.True(await EventuallyAsync(() => connection.Client.Navigated.Any(n => n.Target == LayoutController.Content)));
        Assert.True((await connection.RunAsync("", "creditsCommand")).Success);
        Assert.True(await EventuallyAsync(() => connection.Client.ViewModelOf(LayoutController.Content) == nameof(LogoViewModel)));
        Assert.True((await connection.RunAsync("", "creditsCommand")).Success);
        Assert.True(await EventuallyAsync(() => connection.Client.ViewModelOf(LayoutController.Content) == nameof(CatsViewModel)));
    }

    /// <summary>The left panel of the regions page can be collapsed by a command.</summary>
    [Fact]
    public async Task The_left_panel_of_the_regions_page_is_reduced_by_a_command()
    {
        await using var connection = new Connection(_provider);
        await connection.GoToAsync("/regions");
        Assert.Equal(false, connection.Client.State("")["isLeftCollapsed"]);
        Assert.True((await connection.RunAsync("", "toggleLeftCommand")).Success);
        Assert.True(await EventuallyAsync(() => Equals(connection.Client.State("")["isLeftCollapsed"], true)));
    }

    /// <summary>Tabs navigate inside their own region while the main page stays the same.</summary>
    [Fact]
    public async Task The_tabs_show_their_route_in_the_region_of_the_page()
    {
        await using var connection = new Connection(_provider);
        connection.Session.Watch(TabsViewModel.Region);
        await connection.GoToAsync("/tabs");
        Assert.True((await connection.Session.GoToInAsync(TabsViewModel.Region, "dogs", TestContext.Current.CancellationToken)).Success);
        Assert.Equal(nameof(DogsViewModel), connection.Client.ViewModelOf(TabsViewModel.Region));
        Assert.True((await connection.Session.GoToInAsync(TabsViewModel.Region, "monkeys", TestContext.Current.CancellationToken)).Success);
        Assert.Equal(nameof(MonkeysViewModel), connection.Client.ViewModelOf(TabsViewModel.Region));
    }

    // ---------------------------------------------------------------- adopt

    /// <summary>Adopting pets adds cards; releasing removes them.</summary>
    [Fact]
    public async Task Adopting_a_pet_adds_its_card_to_the_pets_and_releasing_removes_it()
    {
        await using var connection = new Connection(_provider);
        connection.Session.Watch("$pets");
        await connection.GoToAsync("/adopt");
        Assert.Empty((List<object?>)connection.Client.State("$pets")["pets"]!);
        var choices = (List<object?>)connection.Client.State("")["choices"]!;
        Assert.Equal(12, choices.Count);
        var first = (Dictionary<string, object?>)((Dictionary<string, object?>)choices[0]!)["adoptCommand"]!;
        var other = (Dictionary<string, object?>)((Dictionary<string, object?>)choices[3]!)["adoptCommand"]!;
        Assert.True((await connection.Session.InvokeOnAsync("", (string)first["$command"]!, [])).Success);
        Assert.True((await connection.Session.InvokeOnAsync("", (string)other["$command"]!, [])).Success);
        Assert.True(await EventuallyAsync(() => ((List<object?>)connection.Client.State("$pets")["pets"]!).Count == 2));
        Assert.StartsWith("Your pets:", (string)connection.Client.State("")["status"]!, StringComparison.Ordinal);
        Assert.True((await connection.RunAsync("", "releaseCommand")).Success);
        Assert.True(await EventuallyAsync(() => ((List<object?>)connection.Client.State("$pets")["pets"]!).Count == 1));
        Assert.True((await connection.RunAsync("", "releaseAllCommand")).Success);
        Assert.True(await EventuallyAsync(() => ((List<object?>)connection.Client.State("$pets")["pets"]!).Count == 0));
    }

    /// <summary>Adopting the same animal a second time reports that it is already owned.</summary>
    [Fact]
    public async Task Adopting_the_same_animal_twice_says_so()
    {
        await using var connection = new Connection(_provider);
        await connection.GoToAsync("/adopt");
        var choices = (List<object?>)connection.Client.State("")["choices"]!;
        var command = (string)((Dictionary<string, object?>)((Dictionary<string, object?>)choices[0]!)["adoptCommand"]!)["$command"]!;
        await connection.Session.InvokeOnAsync("", command, []);
        await connection.Session.InvokeOnAsync("", command, []);
        Assert.True(await EventuallyAsync(() => ((string)connection.Client.State("")["status"]!).Contains("already yours", StringComparison.Ordinal)));
    }

    // ---------------------------------------------------------------- features, guard, lock

    /// <summary>While signed out the Members route is guarded and redirects to Features.</summary>
    [Fact]
    public async Task Members_is_for_members_signed_out_the_guard_sends_to_Features()
    {
        await using var connection = new Connection(_provider);
        await connection.GoToAsync("/members");
        Assert.Equal(nameof(FeaturesViewModel), connection.Client.ViewModelOf(""));
        Assert.Equal(true, connection.Client.State("")["isSignedOut"]);
    }

    /// <summary>After signing in, Members opens; signing out returns to Features.</summary>
    [Fact]
    public async Task Signed_in_Members_opens_and_signing_out_brings_back_to_Features()
    {
        await using var connection = new Connection(_provider);
        await connection.GoToAsync("/features");
        Assert.True((await connection.RunAsync("", "signInCommand")).Success);
        Assert.True(await EventuallyAsync(() => Equals(connection.Client.State("")["isSignedIn"], true)));
        Assert.True((await connection.RunAsync("", "openMembersCommand")).Success);
        Assert.True(await EventuallyAsync(() => connection.Client.ViewModelOf("") == nameof(MembersViewModel)));
        Assert.Contains("signed in", (string)connection.Client.State("")["welcome"]!, StringComparison.Ordinal);
        Assert.True((await connection.RunAsync("", "signOutCommand")).Success);
        Assert.True(await EventuallyAsync(() => connection.Client.ViewModelOf("") == nameof(FeaturesViewModel)));
    }

    /// <summary>Sign-in state is per connection; one user’s login does not affect another.</summary>
    [Fact]
    public async Task The_sign_in_of_a_connection_is_not_the_sign_in_of_another()
    {
        await using var member = new Connection(_provider);
        await using var visitor = new Connection(_provider);
        await member.GoToAsync("/features");
        await member.RunAsync("", "signInCommand");
        Assert.True(await EventuallyAsync(() => Equals(member.Client.State("")["isSignedIn"], true)));
        await visitor.GoToAsync("/members");
        Assert.Equal(nameof(FeaturesViewModel), visitor.Client.ViewModelOf(""));
        Assert.Equal(true, visitor.Client.State("")["isSignedOut"]);
    }

    /// <summary>A lock disables menu entries until the page unlocks them.</summary>
    [Fact]
    public async Task A_lock_disables_the_menu_until_the_page_unlocks_it()
    {
        await using var connection = new Connection(_provider);
        connection.Session.Watch("$shell");
        await connection.GoToAsync("/features");
        Assert.Equal(false, connection.Client.State("$shell")["isLocked"]);
        Assert.True((await connection.RunAsync("", "lockCommand")).Success);
        Assert.True(await EventuallyAsync(() => Equals(connection.Client.State("$shell")["isLocked"], true)));
        Assert.True(Entries(connection.Client).Count(e => Equals(e["isLocked"], true)) >= 10);
        Assert.True(await EventuallyAsync(() => Equals(connection.Client.State("")["canLock"], false) && Equals(connection.Client.State("")["canUnlock"], true)));
        Assert.True((await connection.RunAsync("", "unlockCommand")).Success);
        Assert.True(await EventuallyAsync(() => Equals(connection.Client.State("$shell")["isLocked"], false)));
        Assert.DoesNotContain(Entries(connection.Client), e => Equals(e["isLocked"], true));
    }

    /// <summary>A timed lock returns immediately and unlocks itself after the delay.</summary>
    [Fact]
    public async Task A_lock_for_some_seconds_returns_at_once_and_ends_by_itself()
    {
        await using var connection = new Connection(_provider);
        connection.Session.Watch("$shell");
        await connection.GoToAsync("/features");
        var started = DateTime.UtcNow;
        Assert.True((await connection.RunAsync("", "lockForCommand")).Success);
        // The call does not wait for the countdown; the menu is locked meanwhile and opens again alone.
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3));
        Assert.True(await EventuallyAsync(() => Equals(connection.Client.State("$shell")["isLocked"], true)));
        Assert.True(await EventuallyAsync(() => Equals(connection.Client.State("$shell")["isLocked"], false), seconds: 15));
    }

    /// <summary>Polls until the condition becomes true or the given number of seconds elapses.</summary>
    private static async Task<bool> EventuallyAsync(Func<bool> condition, int seconds)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < end)
            await Task.Delay(50, TestContext.Current.CancellationToken);
        return condition();
    }
}