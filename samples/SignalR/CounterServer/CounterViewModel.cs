using System.ComponentModel;
using System.Runtime.CompilerServices;
using Routeables.SignalR;

namespace CounterServer;

/// <summary>The page. The browser never sees this class: it gets its state and may call the commands.</summary>
public sealed class CounterView;

public sealed class CounterViewModel : INotifyPropertyChanged
{
    private int _count;
    private int _step = 1;

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Count
    {
        get => _count;
        private set { _count = value; OnPropertyChanged(); }
    }

    /// <summary>Set by the client (a text box); the server checks it.</summary>
    public int Step
    {
        get => _step;
        set { _step = Math.Max(1, value); OnPropertyChanged(); }
    }

    [RouteableCommand]
    public void Increment() => Count += Step;

    [RouteableCommand]
    public void Reset() => Count = 0;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
