using System.Collections.Specialized;
using System.ComponentModel;
using System.Reflection;
using System.Windows.Input;
using Routeables.Abstraction;

namespace Routeables.SignalR;

/// <summary>
/// One thing the client shows: the page of a router, or a view model that never changes (the modal, a registered target). It tells the
/// client each time the page changes (<see cref="IRouteableClient.Navigated"/>) and each time the state of the view model changes
/// (<see cref="IRouteableClient.Changed"/>): the view model itself, but also the collections, the objects and the commands it holds.
/// </summary>
internal sealed class WatchedTarget : IDisposable
{
    private readonly string _name;
    private readonly IRouter? _router;
    private readonly Action<Func<Task>> _send;
    private readonly IRouteableClient _client;
    private readonly object _gate = new();
    private readonly List<Action> _unsubscribe = [];
    private object? _viewModel;
    private bool _disposed;

    public WatchedTarget(string name, IRouteableClient client, Action<Func<Task>> send, IRouter? router, object? viewModel = null)
    {
        _name = name;
        _client = client;
        _send = send;
        _router = router;
        _viewModel = viewModel;
    }

    /// <summary>The view model that is shown now.</summary>
    public object? ViewModel
    {
        get { lock (_gate) return _viewModel; }
    }

    /// <summary>Starts to follow the router and tells the client what is shown now.</summary>
    public void Start()
    {
        if (_router is not null)
        {
            _router.Routed += OnRouted;
            if (_router.CurrentViewModel is { } current)
                Show(current, _router.History.Current?.Args.Route ?? string.Empty);
        }
        else if (_viewModel is { } fixedViewModel)
            Show(fixedViewModel, string.Empty);
    }

    private void OnRouted(object? sender, RouteableRoutedEventArgs e)
    {
        if (e.Succeeded)
            Show(e.To.ViewModel, e.To.Args?.Route ?? string.Empty);
    }

    private void Show(object viewModel, string route)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            Unsubscribe();
            _viewModel = viewModel;
            Subscribe(viewModel);
        }

        RouteableNavigated message;
        try
        {
            message = new RouteableNavigated(
                route, viewModel.GetType().Name, RouteableStateProjector.Project(viewModel),
                RouteableStateProjector.Commands(viewModel.GetType()).Keys.ToList(), _name);
        }
        catch (InvalidOperationException)
        {
            // The state changed while it was read (a collection): the change that did it tells the client again.
            return;
        }
        _send(() => _client.Navigated(message));
    }

    private void Subscribe(object viewModel)
    {
        foreach (var (source, holder) in RouteableStateProjector.Sources(viewModel))
        {
            if (source is INotifyPropertyChanged properties)
            {
                PropertyChangedEventHandler handler = (sender, e) => OnChanged(holder, e.PropertyName);
                properties.PropertyChanged += handler;
                _unsubscribe.Add(() => properties.PropertyChanged -= handler);
            }
            if (source is INotifyCollectionChanged collection)
            {
                NotifyCollectionChangedEventHandler handler = (sender, e) => OnChanged(holder, null);
                collection.CollectionChanged += handler;
                _unsubscribe.Add(() => collection.CollectionChanged -= handler);
            }
            if (source is ICommand command)
            {
                EventHandler handler = (sender, e) => OnChanged(holder, null);
                command.CanExecuteChanged += handler;
                _unsubscribe.Add(() => command.CanExecuteChanged -= handler);
            }
        }
    }

    private void Unsubscribe()
    {
        foreach (var undo in _unsubscribe)
            undo();
        _unsubscribe.Clear();
    }

    // holder: the property of the view model that holds the object that changed; null when the view model itself changed.
    private void OnChanged(string? holder, string? propertyName)
    {
        object? viewModel;
        lock (_gate)
        {
            if (_disposed || _viewModel is null)
                return;
            viewModel = _viewModel;
        }

        var properties = RouteableStateProjector.StateProperties(viewModel.GetType()).ToList();
        IEnumerable<PropertyInfo> changed;
        if (holder is not null)
            changed = properties.Where(p => RouteableStateProjector.ClientName(p.Name) == holder);
        else if (string.IsNullOrEmpty(propertyName))
            changed = properties;
        else
            changed = properties.Where(p => p.Name == propertyName);

        foreach (var property in changed)
            SendProperty(viewModel, property);

        // The collections and the objects the view model holds may be others now.
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(_viewModel, viewModel))
                return;
            Unsubscribe();
            Subscribe(viewModel);
        }
    }

    private void SendProperty(object viewModel, PropertyInfo property)
    {
        RouteableChanged message;
        try
        {
            var name = RouteableStateProjector.ClientName(property.Name);
            var value = property.GetValue(viewModel);
            message = new RouteableChanged(name, value is null ? null : RouteableStateProjector.ProjectValue(value, name), _name);
        }
        catch (TargetInvocationException) { return; }
        catch (InvalidOperationException) { return; }

        _send(() => _client.Changed(message));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            Unsubscribe();
            _viewModel = null;
        }
        if (_router is not null)
            _router.Routed -= OnRouted;
    }
}
