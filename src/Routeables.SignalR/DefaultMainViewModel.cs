using System.Collections.ObjectModel;
using System.ComponentModel;
using Routeables.Abstraction;
using Routeables.Core;

namespace Routeables.SignalR;

/// <summary>
/// The main view model of an application that has no menu, the one <c>AddRouteablesSignalR()</c> registers when no type is given.
/// It lists no entry: the client asks for the pages it needs.
/// </summary>
/// <param name="router">The router of the connection.</param>
internal sealed class DefaultMainViewModel(IRouter router) : MainViewModelBase(router, initialize: false), INotifyPropertyChanged, IMainViewModel
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <inheritdoc />
    protected override void NotifyPropertyChanged(string? propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>No entry: the application has no menu.</summary>
    protected override ObservableCollection<IRouteableEntry> GetRouteables() => [];
}
