using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Authorization;
using Routeables.Abstraction;
using Routeables.Services;
using Routeables.SignalR;

namespace Routeables.SignalR.E2E;

/// <summary>The "view" of every route: the browser draws the state, the server has no view.</summary>
public sealed class Page;

/// <summary>A page with a value, a list, a long command, a dialog and a command for managers.</summary>
[RouteableState]
public sealed class StreamViewModel(IRouteableDialogService dialogs) : INotifyPropertyChanged
{
    private int _value;
    private string _answer = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Value { get => _value; private set { _value = value; Raise(); } }

    public string Answer { get => _answer; private set { _answer = value; Raise(); } }

    private string _search = string.Empty;

    [RouteableState(Settable = true)]
    public string Search { get => _search; set { _search = value; Raise(); } }

    public ObservableCollection<string> Words { get; } = [];

    [RouteableCommand]
    public void Count(int times)
    {
        for (var i = 1; i <= times; i++)
            Value = i;
    }

    [RouteableCommand]
    public void Add(string word) => Words.Add(word);

    [RouteableCommand]
    public async Task SlowAsync(int milliseconds)
    {
        await Task.Delay(milliseconds);
        Value++;
    }

    [RouteableCommand]
    public async Task AskAsync() => Answer = await dialogs.ShowYesNoAsync("Go on?", "Ask") ? "yes" : "no";

    [RouteableCommand, Authorize(Roles = "manager")]
    public void Approve() => Answer = "approved";

    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A page that asks before it lets the visitor leave: a route change that waits for an answer.</summary>
[RouteableState]
public sealed class GuardedViewModel(IRouteableDialogService dialogs) : IRouteable
{
    public string Name => "guarded";

    public Task<bool> CanLeaveAsync(CancellationToken ct = default) => dialogs.ShowYesNoAsync("Leave without saving?", "Leave");
}

/// <summary>A page only managers may open.</summary>
[RouteableState, Authorize(Roles = "manager")]
public sealed class AdminViewModel
{
    public string Secret => "for managers";
}
