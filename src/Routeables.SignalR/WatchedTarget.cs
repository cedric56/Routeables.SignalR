using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Routeables.Abstraction;
using Routeables.Abstraction.Routing;

namespace Routeables.SignalR;

/// <summary>
/// One thing the client shows: the page of a router, or a view model that never changes (the modal, a registered target). It tells the client
/// each time the page changes (<see cref="IRouteableClient.Navigated"/>, a new generation) and each time the state of the view model changes
/// (<see cref="IRouteableClient.Changed"/>): the view model itself, but also the collections, the objects and the commands it holds.
/// </summary>
/// <remarks>
/// Everything runs on the dispatcher of the session; an event raised on another thread is brought there. The changes of a turn of the dispatcher are
/// gathered and told at its end, as one message: a property set a thousand times in a loop is sent once, with its last value.
/// </remarks>
internal sealed class WatchedTarget : IDisposable
{
    private static readonly ConditionalWeakTable<object, StrongBox<long>> Instances = new();
    private static long _nextInstance;

    private readonly RouteableSession _session;
    private readonly IRouter? _router;
    private readonly Dictionary<string, List<Action>> _subscriptions = new(StringComparer.Ordinal);
    private readonly List<Action> _rootSubscription = [];
    private readonly HashSet<string> _resubscribe = new(StringComparer.Ordinal);
    private readonly List<PendingPatch> _pending = [];
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _retries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _reveals = new(StringComparer.Ordinal);
    private readonly NestedReadAccess _nested;
    private readonly ReadGuard _guard;
    private HashSet<PropertyInfo> _hidden = [];
    private bool _deciding;
    private object? _viewModel;
    private string _route = string.Empty;
    private bool _visible;
    private bool _resubscribeAll;
    private bool _disposed;

    public WatchedTarget(string name, RouteableSession session, IRouter? router, object? viewModel = null)
    {
        Name = name;
        _session = session;
        _router = router;
        _viewModel = viewModel;
        _nested = new NestedReadAccess(session.Authorizer);
        _guard = _nested.Allows;
    }

    public string Name { get; }

    /// <summary>Grows each time the target shows a page, or sends its state again.</summary>
    public long Generation { get; private set; }

    /// <summary>The view model that is shown now, when the user may see it.</summary>
    public object? ViewModel => _visible ? _viewModel : null;

    /// <summary>Whether something waits to be told at the end of the turn.</summary>
    public bool HasPending => _pending.Count > 0;

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

    /// <summary>Sends the full state again (a resync, a resumed session): a new generation of the same page.</summary>
    public void Resend()
    {
        if (_viewModel is { } viewModel)
            Show(viewModel, _route);
    }

    private void OnRouted(object? sender, RouteableRoutedEventArgs e)
    {
        if (e.Succeeded)
            _session.Dispatcher.Run(() => Show(e.To.ViewModel, e.To.Args?.Route ?? string.Empty));
    }

    private void Show(object viewModel, string route) => _ = ShowAsync(viewModel, route);

    private async Task ShowAsync(object viewModel, string route)
    {
        if (_disposed)
            return;

        var generation = ++Generation;
        var previous = _visible;
        UnsubscribeAll();
        _pending.Clear();
        _reported.Clear();
        _retries.Clear();
        _reveals.Clear();
        _nested.Clear();
        _viewModel = viewModel;
        _route = route;
        _visible = false;

        var shape = _session.Schema.Shape(viewModel.GetType());
        bool allowed;
        HashSet<PropertyInfo> hidden;
        IReadOnlyList<string> commands;
        try
        {
            allowed = await _session.Authorizer.CanSeeAsync(viewModel, shape).ConfigureAwait(true);
            hidden = allowed ? await _session.Authorizer.HiddenAsync(viewModel, shape).ConfigureAwait(true) : [];
            commands = allowed ? await AllowedCommandsAsync(viewModel, shape).ConfigureAwait(true) : [];

            // The properties of the objects inside the state that ask for an authorization: decided before the page is sent, so that it is sent
            // whole. A projection meets them, they are decided, and what they hold may hold more (bounded by the depth of a state).
            for (var round = 0; allowed && round <= _session.Schema.Options.MaxDepth; round++)
            {
                var seen = hidden;
                _session.Projector.Project(viewModel, null, property => !seen.Contains(property), _guard);
                if (!_nested.HasUnknown)
                    break;
                await _nested.DecideAsync(_session.Unhandled).ConfigureAwait(true);
                if (_disposed || generation != Generation)
                    return;
            }
        }
        catch (Exception exception)
        {
            _session.Unhandled(exception);
            allowed = false;
            hidden = [];
            commands = [];
        }

        if (_disposed || generation != Generation)
            return;   // another page came while this one was authorized

        if (!allowed)
        {
            _session.Report(new RouteableFailure(route, $"You may not see '{viewModel.GetType().Name}'.", Name, RouteableErrorCodes.Unauthorized));
            if (previous)
                _session.Outbound.Closed(new RouteableClosed(Name, generation));
            return;
        }

        _hidden = hidden;
        _visible = true;
        Subscribe(viewModel);

        var state = _session.Projector.Project(viewModel, issue => Report(issue), property => !_hidden.Contains(property), _guard);
        _session.BeforeMessage();
        _session.Outbound.Navigated(new RouteableNavigated(route, viewModel.GetType().Name, state, commands, Name, generation, InstanceOf(viewModel)));
        DecideLater();
    }

    // Something the projection met was not decided (it was not sent): it is decided, and the properties of the view model that hold what the user
    // may read are sent again. An object made anew at each reading is never decided: it is sent again a few times per change, then left hidden.
    private void DecideLater()
    {
        if (_deciding || !_nested.HasUnknown)
            return;
        _deciding = true;
        // A turn of its own: a decision may be taken at once, and what it sends again is then told at the end of that turn.
        _session.Dispatcher.Post(_ => _ = DecideLaterAsync(), null);
    }

    private async Task DecideLaterAsync()
    {
        var generation = Generation;
        HashSet<string> reveal;
        try { reveal = await _nested.DecideAsync(_session.Unhandled).ConfigureAwait(true); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _session.Unhandled(exception);
            return;   // what was not decided stays hidden
        }
        finally { _deciding = false; }

        if (generation != Generation || _viewModel is not { } viewModel || !IsCurrent(viewModel))
            return;
        var shape = _session.Schema.Shape(viewModel.GetType());
        var any = false;
        foreach (var name in reveal)
        {
            if (!shape.ByName.TryGetValue(name, out var member) || (_reveals[name] = _reveals.GetValueOrDefault(name) + 1) > 3)
                continue;
            RecordSet(member);
            any = true;
        }
        if (any)
            _session.MarkPending(this);
        DecideLater();   // what a projection met while this was decided
    }

    private async Task<IReadOnlyList<string>> AllowedCommandsAsync(object viewModel, TypeShape shape)
    {
        var names = new List<string>();
        foreach (var (name, command) in shape.Commands)
            if (await _session.Authorizer.CanInvokeAsync(viewModel, shape, command).ConfigureAwait(true))
                names.Add(name);
        return names;
    }

    private static long InstanceOf(object viewModel)
        => Instances.GetValue(viewModel, static _ => new StrongBox<long>(Interlocked.Increment(ref _nextInstance))).Value;

    // ---------------------------------------------------------------- subscriptions

    private void Subscribe(object viewModel)
    {
        if (viewModel is INotifyPropertyChanged root)
        {
            PropertyChangedEventHandler handler = (_, e) => OnViewModelChanged(viewModel, e.PropertyName);
            root.PropertyChanged += handler;
            _rootSubscription.Add(() => root.PropertyChanged -= handler);
        }
        foreach (var member in _session.Schema.Shape(viewModel.GetType()).State)
            SubscribeProperty(viewModel, member);
    }

    private void SubscribeProperty(object viewModel, StateMember member)
    {
        if (_hidden.Contains(member.Property))
            return;

        var undo = new List<Action>();
        foreach (var (source, _) in _session.Projector.SourcesOf(viewModel, member))
        {
            // A collection tells its Count and its indexer as properties too: its CollectionChanged says more.
            if (source is INotifyPropertyChanged properties && source is not INotifyCollectionChanged)
            {
                PropertyChangedEventHandler handler = (_, e) => OnInnerChanged(viewModel, member, source, e.PropertyName);
                properties.PropertyChanged += handler;
                undo.Add(() => properties.PropertyChanged -= handler);
            }
            if (source is INotifyCollectionChanged collection)
            {
                NotifyCollectionChangedEventHandler handler = (_, e) => OnCollectionChanged(viewModel, member, source, e);
                collection.CollectionChanged += handler;
                undo.Add(() => collection.CollectionChanged -= handler);
            }
            if (source is ICommand command)
            {
                EventHandler handler = (_, _) => OnInnerChanged(viewModel, member, source, null);
                command.CanExecuteChanged += handler;
                undo.Add(() => command.CanExecuteChanged -= handler);
            }
        }
        _subscriptions[member.Name] = undo;
    }

    private void Unsubscribe(string property)
    {
        if (_subscriptions.Remove(property, out var undo))
            foreach (var action in undo)
                action();
    }

    private void UnsubscribeAll()
    {
        foreach (var action in _rootSubscription)
            action();
        _rootSubscription.Clear();
        foreach (var undo in _subscriptions.Values)
            foreach (var action in undo)
                action();
        _subscriptions.Clear();
        _resubscribe.Clear();
        _resubscribeAll = false;
    }

    // ---------------------------------------------------------------- changes

    // The view model raised PropertyChanged: one property, or all of them (null or empty name).
    private void OnViewModelChanged(object viewModel, string? propertyName)
    {
        if (!_session.Dispatcher.CheckAccess())
        {
            _session.Dispatcher.Run(() => OnViewModelChanged(viewModel, propertyName));
            return;
        }
        if (!IsCurrent(viewModel))
            return;

        var shape = _session.Schema.Shape(viewModel.GetType());
        if (string.IsNullOrEmpty(propertyName))
        {
            _reveals.Clear();
            foreach (var member in shape.State)
                RecordSet(member);
            _resubscribeAll = true;
        }
        else if (shape.State.FirstOrDefault(m => m.Property.Name == propertyName) is { } member)
        {
            _reveals.Remove(member.Name);
            RecordSet(member);
            _resubscribe.Add(member.Name);
        }
        _session.MarkPending(this);
    }

    // An object inside a property changed (an item, a nested object, a command).
    private void OnInnerChanged(object viewModel, StateMember member, object source, string? propertyName)
    {
        if (!_session.Dispatcher.CheckAccess())
        {
            _session.Dispatcher.Run(() => OnInnerChanged(viewModel, member, source, propertyName));
            return;
        }
        if (!IsCurrent(viewModel))
            return;
        _reveals.Remove(member.Name);

        // An item of a list held by the property: only that item is sent again.
        if (source is not ICommand
            && RouteableStateProjector.TryRead(viewModel, member.Property, out var value) && value is IList list && !ReferenceEquals(list, source)
            && IndexOf(list, source) is var index and >= 0)
        {
            var path = member.Name + "." + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var item = _session.Projector.ProjectAt(member.Name, path, source, depth: 2, issue => Report(issue), _guard);
            RecordPatch(member, new RouteablePatch(RouteablePatch.Set, [member.Name, index], item), coalesceLast: true);
        }
        else
            RecordSet(member);

        _resubscribe.Add(member.Name);
        _session.MarkPending(this);
    }

    private void OnCollectionChanged(object viewModel, StateMember member, object source, NotifyCollectionChangedEventArgs e)
    {
        if (!_session.Dispatcher.CheckAccess())
        {
            // The indexes of the event are those of a state that went on changing: the whole value is sent again.
            _session.Dispatcher.Run(() => OnInnerChanged(viewModel, member, source, null));
            return;
        }
        if (!IsCurrent(viewModel))
            return;
        _reveals.Remove(member.Name);

        if (Splices(viewModel, member, source, e) is { } splices)
            foreach (var splice in splices)
                RecordPatch(member, splice, coalesceLast: false);
        else
            RecordSet(member);

        _resubscribe.Add(member.Name);
        _session.MarkPending(this);
    }

    // The patches that tell a change of the list held by the property, or null when the whole value is sent again.
    private List<RouteablePatch>? Splices(object viewModel, StateMember member, object source, NotifyCollectionChangedEventArgs e)
    {
        if (!RouteableStateProjector.TryRead(viewModel, member.Property, out var value) || !ReferenceEquals(value, source) || source is not IList list)
            return null;
        if (list.Count > _session.Schema.Options.MaxCollectionItems || _session.Schema.MayHoldCommands(source.GetType()))
            return null;   // too large (the limit is told by a full set), or items whose commands would keep their old index

        var patches = new List<RouteablePatch>();
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewStartingIndex >= 0 && e.NewItems is not null:
                patches.Add(Splice(member, e.NewStartingIndex, 0, e.NewItems));
                break;
            case NotifyCollectionChangedAction.Remove when e.OldStartingIndex >= 0 && e.OldItems is not null:
                patches.Add(Splice(member, e.OldStartingIndex, e.OldItems.Count, null));
                break;
            case NotifyCollectionChangedAction.Replace when e.NewStartingIndex >= 0 && e.OldItems is not null && e.NewItems is not null:
                patches.Add(Splice(member, e.NewStartingIndex, e.OldItems.Count, e.NewItems));
                break;
            case NotifyCollectionChangedAction.Move when e.OldStartingIndex >= 0 && e.NewStartingIndex >= 0 && e.OldItems is not null:
                patches.Add(Splice(member, e.OldStartingIndex, e.OldItems.Count, null));
                patches.Add(Splice(member, e.NewStartingIndex, 0, e.OldItems));
                break;
            default:
                return null;
        }
        return patches;
    }

    private RouteablePatch Splice(StateMember member, int index, int remove, IList? items)
    {
        var projected = new List<object?>();
        if (items is not null)
            for (var i = 0; i < items.Count; i++)
            {
                var path = member.Name + "." + (index + i).ToString(System.Globalization.CultureInfo.InvariantCulture);
                projected.Add(_session.Projector.ProjectAt(member.Name, path, items[i], depth: 2, issue => Report(issue), _guard));
            }
        return new RouteablePatch(RouteablePatch.Splice, [member.Name], projected, index, remove);
    }

    private static int IndexOf(IList list, object item)
    {
        for (var i = 0; i < list.Count; i++)
            if (ReferenceEquals(list[i], item))
                return i;
        return -1;
    }

    private bool IsCurrent(object viewModel) => !_disposed && _visible && ReferenceEquals(_viewModel, viewModel);

    // The value of a property is sent again; it is read at the end of the turn, so that it is read once.
    private void RecordSet(StateMember member)
    {
        if (_hidden.Contains(member.Property))
            return;
        _pending.RemoveAll(p => p.Member == member);
        _pending.Add(new PendingPatch(member, null));
    }

    // A patch read now (its indexes are those of now). Nothing is added when the whole value is sent again anyway.
    private void RecordPatch(StateMember member, RouteablePatch patch, bool coalesceLast)
    {
        if (_hidden.Contains(member.Property) || _pending.Any(p => p.Member == member && p.Patch is null))
            return;
        if (coalesceLast && _pending.LastOrDefault(p => p.Member == member) is { Patch: { Op: RouteablePatch.Set } last } previous
            && RouteablePatches.StartsWith(last.Path, patch.Path) && last.Path.Count == patch.Path.Count)
            _pending.Remove(previous);
        _pending.Add(new PendingPatch(member, patch));
    }

    /// <summary>Tells what changed during the turn, as one message, and follows the objects that the changed properties hold now.</summary>
    public void Flush()
    {
        if (_disposed || !_visible || _viewModel is not { } viewModel)
        {
            _pending.Clear();
            return;
        }

        if (_resubscribeAll)
        {
            foreach (var member in _session.Schema.Shape(viewModel.GetType()).State)
            {
                Unsubscribe(member.Name);
                SubscribeProperty(viewModel, member);
            }
        }
        else
        {
            foreach (var name in _resubscribe)
            {
                Unsubscribe(name);
                if (_session.Schema.Shape(viewModel.GetType()).ByName.TryGetValue(name, out var member))
                    SubscribeProperty(viewModel, member);
            }
        }
        _resubscribe.Clear();
        _resubscribeAll = false;

        if (_pending.Count == 0)
            return;

        var patches = new List<RouteablePatch>();
        foreach (var pending in _pending)
        {
            if (pending.Patch is not null)
            {
                RouteablePatches.Add(patches, pending.Patch);
                continue;
            }
            if (!RouteableStateProjector.TryRead(viewModel, pending.Member.Property, out var value, out var failure))
            {
                Report(RouteableStateProjector.ReadFailure(pending.Member.Name, failure!));
                continue;
            }
            var projected = _session.Projector.ProjectAt(pending.Member.Name, pending.Member.Name, value, depth: 1, issue => Report(issue), _guard);
            RouteablePatches.Add(patches, new RouteablePatch(RouteablePatch.Set, [pending.Member.Name], projected));
        }
        _pending.Clear();

        if (patches.Count > 0)
            _session.Outbound.Changed(Name, Generation, patches);
        DecideLater();
    }

    private void Report(RouteableProjectionIssue issue)
    {
        RouteablesTelemetry.ProjectionIssues.Add(1, new KeyValuePair<string, object?>("code", issue.Code));
        if (issue.Retry && (_retries[issue.Property] = _retries.GetValueOrDefault(issue.Property) + 1) <= 3)
        {
            // Read it again once the change that disturbed the reading has been brought to the session.
            if (_viewModel is { } viewModel && _session.Schema.Shape(viewModel.GetType()).ByName.TryGetValue(issue.Property, out var member))
                _session.Dispatcher.Post(_ =>
                {
                    if (!IsCurrent(viewModel))
                        return;
                    RecordSet(member);
                    _session.MarkPending(this);
                }, null);
            return;
        }

        if (!_reported.Add(issue.Property + "|" + issue.Code))
            return;
        // What a getter threw may say more than the visitor should know (a connection string, a table): its message is sent in Development only,
        // or when it is a RouteableException, meant for the visitor. The server logs it.
        var message = issue.Message;
        if (issue.Exception is { } exception)
        {
            _session.LogProjectionFailure(exception, Name, issue.Property);
            if (!_session.DetailedErrors && exception is not RouteableException)
                message = $"'{issue.Property}' could not be read.";
            else if (exception is RouteableException)
                message = exception.Message;
        }
        _session.Report(new RouteableFailure(_route, message, Name, issue.Code));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        UnsubscribeAll();
        _pending.Clear();
        _viewModel = null;
        _visible = false;
        if (_router is not null)
            _router.Routed -= OnRouted;
    }

    private sealed record PendingPatch(StateMember Member, RouteablePatch? Patch);
}
