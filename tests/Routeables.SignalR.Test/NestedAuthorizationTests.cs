using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using System.Windows.Input;
using Microsoft.AspNetCore.Authorization;
using Routeables.Abstraction;
using Routeables.Core;
using Routeables.SignalR;

namespace Routeables.Tests.Remote;

/// <summary>A customer inside a page: its notes are for managers only.</summary>
[RouteableState]
public sealed class CustomerDetails : INotifyPropertyChanged
{
    private string _internalNotes;

    public CustomerDetails(string name, string internalNotes)
    {
        Name = name;
        _internalNotes = internalNotes;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; }

    [RouteableAuthorize(Roles = "manager")]
    public string InternalNotes { get => _internalNotes; set { _internalNotes = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InternalNotes))); } }
}

/// <summary>A type that only managers may read, wherever it is.</summary>
[RouteableState, Authorize(Roles = "manager")]
public sealed class Ledger
{
    public decimal Balance => 98765.4321m;

    public ICommand CloseCommand { get; } = new TestCommand(() => Task.CompletedTask);
}

[RouteableState]
public sealed class CustomerOrderViewModel : INotifyPropertyChanged
{
    private CustomerDetails _customer = new("Ada", "pays late");

    public event PropertyChangedEventHandler? PropertyChanged;

    public CustomerDetails Customer { get => _customer; set { _customer = value; Raise(); } }

    public ObservableCollection<CustomerDetails> Others { get; } = [new("Bob", "asked for a refund")];

    public Ledger Ledger { get; } = new();

    public string Title => "Order 7";

    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// What a user may read of the objects inside a page: the authorization of a nested type and of its properties applies to what is sent, as it applies
/// to the commands at the same path. Checked on the messages as the client receives them (serialized).
/// </summary>
public class NestedAuthorizationTests
{
    private static ClaimsPrincipal User(string name, params string[] roles)
        => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, name), .. roles.Select(r => new Claim(ClaimTypes.Role, r))], "test"));

    private static ServiceProvider Services()
    {
        var services = TestSchema.Services();
        services.AddAuthorizationCore();
        services.RegisterRoute<OrdersView, CustomerOrderViewModel>(route: "/customer-order");
        return services.BuildServiceProvider();
    }

    private static string Sent(RecordingClient client) => JsonSerializer.Serialize(client.All.ToArray());

    private static async Task<(RouteableSession Session, RecordingClient Client)> OpenAsync(ServiceProvider provider, ClaimsPrincipal user)
    {
        var client = new RecordingClient();
        var session = new RouteableSession(provider, client, user);
        await session.GoToAsync("/customer-order", TestContext.Current.CancellationToken);
        await session.Dispatcher.IdleAsync();
        return (session, client);
    }

    [Fact]
    public async Task A_nested_property_that_asks_for_a_role_is_never_sent_to_who_has_not_it()
    {
        await using var provider = Services();
        var (clerk, clerkClient) = await OpenAsync(provider, User("clerk"));
        var (manager, managerClient) = await OpenAsync(provider, User("boss", "manager"));
        await using var _ = clerk;
        await using var __ = manager;

        var clerkSaw = Sent(clerkClient);
        Assert.DoesNotContain("pays late", clerkSaw);
        Assert.DoesNotContain("asked for a refund", clerkSaw);
        Assert.DoesNotContain("internalNotes", clerkSaw);
        Assert.Contains("Ada", clerkSaw);

        // The page is sent whole to who may read it: the decisions are taken before the page leaves.
        var state = managerClient.Navigated[0].State;
        Assert.Equal("pays late", ((IReadOnlyDictionary<string, object?>)state["customer"]!)["internalNotes"]);
        Assert.Contains("asked for a refund", Sent(managerClient));
    }

    [Fact]
    public async Task A_nested_type_that_asks_for_a_role_is_sent_empty_and_its_commands_cannot_be_called()
    {
        await using var provider = Services();
        var (clerk, clerkClient) = await OpenAsync(provider, User("clerk"));
        var (manager, managerClient) = await OpenAsync(provider, User("boss", "manager"));
        await using var _ = clerk;
        await using var __ = manager;

        Assert.Empty((IReadOnlyDictionary<string, object?>)clerkClient.Navigated[0].State["ledger"]!);
        Assert.DoesNotContain("98765", Sent(clerkClient));
        Assert.DoesNotContain("closeCommand", Sent(clerkClient));
        Assert.Equal(RouteableErrorCodes.Unauthorized, (await clerk.InvokeAsync("ledger.closeCommand", [])).Code);

        Assert.Equal(98765.4321m, ((IReadOnlyDictionary<string, object?>)managerClient.Navigated[0].State["ledger"]!)["balance"]);
        Assert.True((await manager.InvokeAsync("ledger.closeCommand", [])).Success);
    }

    [Fact]
    public async Task What_changes_inside_the_state_is_filtered_too()
    {
        await using var provider = Services();
        var (clerk, clerkClient) = await OpenAsync(provider, User("clerk"));
        var (manager, managerClient) = await OpenAsync(provider, User("boss", "manager"));
        await using var _ = clerk;
        await using var __ = manager;
        var clerkPage = (CustomerOrderViewModel)clerk.Current!;
        var managerPage = (CustomerOrderViewModel)manager.Current!;

        // A nested property that changes, a new nested object, a new item of a list.
        foreach (var (session, page) in new[] { (clerk, clerkPage), (manager, managerPage) })
        {
            await session.Dispatcher.InvokeAsync(() => page.Customer.InternalNotes = "secret one");
            await session.Dispatcher.InvokeAsync(() => page.Customer = new CustomerDetails("Eve", "secret two"));
            await session.Dispatcher.InvokeAsync(() => page.Others.Add(new CustomerDetails("Mallory", "secret three")));
        }

        // The objects met for the first time are decided after they are sent: they are sent again with what the user may read.
        Assert.True(await TestSchema.EventuallyAsync(() => Sent(managerClient) is var sent
            && sent.Contains("secret one") && sent.Contains("secret two") && sent.Contains("secret three")));
        await clerk.Dispatcher.IdleAsync();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await clerk.Dispatcher.IdleAsync();

        var clerkSaw = Sent(clerkClient);
        Assert.Contains("Eve", clerkSaw);
        Assert.Contains("Mallory", clerkSaw);
        Assert.DoesNotContain("secret", clerkSaw);
    }
}
