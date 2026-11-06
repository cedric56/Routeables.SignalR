using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Routeables.Abstraction;

namespace Routeables.SignalR;

/// <summary>
/// What a connection owns: its routers, with their histories, and the view models they created. The connection is the lifetime, as
/// the circuit is for Blazor Server: when it closes, the session is disposed and the view models with it.
/// The client sees <em>targets</em> (see <see cref="RouteableTargets"/>): the page of the main router is always watched; the regions and the
/// registered view models are watched when the client asks (<see cref="Watch"/>).
/// </summary>
public sealed class RouteableSession : IAsyncDisposable
{
    /// <summary>How many targets a client may watch.</summary>
    public const int MaxTargets = 24;

    // A command that opens a dialog does not end before the visitor answered: the call returns once it asked, or after this time.
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(800);

    private readonly AsyncServiceScope _scope;
    private readonly IRouteableClient _client;
    private readonly IRouter _router;
    private readonly RemoteInteractions _interactions;
    private readonly IReadOnlyDictionary<string, Type> _registered;
    private readonly ConcurrentDictionary<string, WatchedTarget> _targets = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>Creates the session of one connection.</summary>
    /// <param name="services">The application services; the session opens its own scope.</param>
    /// <param name="client">Where the session sends what changes.</param>
    public RouteableSession(IServiceProvider services, IRouteableClient client)
    {
        ArgumentNullException.ThrowIfNull(services);
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _scope = services.CreateAsyncScope();
        _router = _scope.ServiceProvider.GetRequiredService<IRouter>();
        _registered = _scope.ServiceProvider.GetServices<RouteableTarget>().ToDictionary(t => t.Name, t => t.ViewModel, StringComparer.Ordinal);

        _interactions = _scope.ServiceProvider.GetRequiredService<RemoteInteractions>();
        _interactions.DialogRequested += OnDialog;
        _interactions.ToastRequested += OnToast;
        _interactions.ModalOpened += OnModalOpened;
        _interactions.ModalClosed += OnModalClosed;

        // The page of the main router is told to the client whoever asked for it: the client (GoTo) or a command of a view model that navigates.
        Put(RouteableTargets.Page, new WatchedTarget(RouteableTargets.Page, _client, Send, _router));
    }

    /// <summary>The view model of the page that is current, or <see langword="null"/>.</summary>
    public object? Current => _router.CurrentViewModel;

    // ---------------------------------------------------------------- targets

    /// <summary>Starts to tell the client what a target shows: a region, or a view model registered with <c>AddRouteableTarget</c>.</summary>
    public RouteableAck Watch(string target)
    {
        if (string.IsNullOrEmpty(target) || target == RouteableTargets.Modal)
            return RouteableAck.Ok;   // the page and the modal are always watched

        if (_targets.ContainsKey(target))
            return RouteableAck.Ok;

        if (_targets.Count >= MaxTargets)
            return RouteableAck.Fail($"At most {MaxTargets} targets can be watched.");

        WatchedTarget watched;
        if (target.StartsWith('$'))
        {
            if (!_registered.TryGetValue(target, out var type))
                return RouteableAck.Fail($"No target is registered under '{target}'.");
            watched = new WatchedTarget(target, _client, Send, null, _scope.ServiceProvider.GetRequiredService(type));
        }
        else
        {
            var router = FindRouter(target, out var error);
            if (router is null)
                return RouteableAck.Fail(error!);
            watched = new WatchedTarget(target, _client, Send, router);
        }

        if (!_targets.TryAdd(target, watched))
        {
            watched.Dispose();
            return RouteableAck.Ok;
        }
        watched.Start();
        return RouteableAck.Ok;
    }

    /// <summary>Stops telling the client about a target (the main page and the modal stay).</summary>
    public RouteableAck Unwatch(string target)
    {
        if (!string.IsNullOrEmpty(target) && target != RouteableTargets.Modal && _targets.TryRemove(target, out var watched))
            watched.Dispose();
        return RouteableAck.Ok;
    }

    private IRouter? FindRouter(string target, out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(target))
            return _router;

        try { return _scope.ServiceProvider.GetRouter(target); }
        catch (InvalidOperationException exception)
        {
            error = exception.Message;
            return null;
        }
    }

    private void Put(string name, WatchedTarget target)
    {
        _targets[name] = target;
        target.Start();
    }

    // ---------------------------------------------------------------- what the client asks

    /// <summary>Goes to <paramref name="route"/> in the main router. The client is told by <see cref="IRouteableClient.Navigated"/> when it succeeds.</summary>
    public Task<RouteableAck> GoToAsync(string route, CancellationToken cancellationToken = default)
        => GoToInAsync(RouteableTargets.Page, route, cancellationToken);

    /// <summary>Goes to <paramref name="route"/> in the router of a target (the main router, or a region).</summary>
    public async Task<RouteableAck> GoToInAsync(string target, string route, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(route))
            return RouteableAck.Fail("The route is empty.");

        var router = FindRouter(target, out var error);
        if (router is null)
            return RouteableAck.Fail(error!);

        var result = await router.GoToAsync(route, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.Success)
            return RouteableAck.Ok;

        var message = result.Exception?.Message ?? $"The route '{route}' could not be opened.";
        await _client.Failed(new RouteableFailure(route, message)).ConfigureAwait(false);
        return RouteableAck.Fail(message);
    }

    /// <summary>Calls a command of the current page.</summary>
    public Task<RouteableAck> InvokeAsync(string command, IReadOnlyList<JsonElement>? arguments)
        => InvokeOnAsync(RouteableTargets.Page, command, arguments);

    /// <summary>Calls a command of the view model that a target shows.</summary>
    public Task<RouteableAck> InvokeOnAsync(string target, string command, IReadOnlyList<JsonElement>? arguments)
        => ViewModelOf(target) is { } viewModel
            ? RunAsync(() => RouteableCommandInvoker.InvokeAsync(viewModel, command, arguments))
            : Task.FromResult(NoViewModel(target));

    /// <summary>Sets a property of the current page (what a text box or a check box does).</summary>
    public RouteableAck Set(string property, JsonElement value) => SetOn(RouteableTargets.Page, property, value);

    /// <summary>Sets a property of the view model that a target shows.</summary>
    public RouteableAck SetOn(string target, string property, JsonElement value)
    {
        if (ViewModelOf(target) is not { } viewModel)
            return NoViewModel(target);

        var member = RouteableStateProjector.StateProperties(viewModel.GetType())
            .FirstOrDefault(p => string.Equals(RouteableStateProjector.ClientName(p.Name), property, StringComparison.Ordinal));

        if (member is not { SetMethod.IsPublic: true })
            return RouteableAck.Fail($"'{viewModel.GetType().Name}' has no property '{property}' that can be set.");

        try
        {
            var converted = RouteableStateProjector.FromString(member.PropertyType) is { } fromString && value.ValueKind == JsonValueKind.String
                ? fromString.Invoke(null, [value.GetString()])
                : value.Deserialize(member.PropertyType, JsonSerializerOptions.Web);
            member.SetValue(viewModel, converted);
            return RouteableAck.Ok;
        }
        catch (JsonException)
        {
            return RouteableAck.Fail($"The value of '{property}' is not a {member.PropertyType.Name}.");
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            return RouteableAck.Fail(exception.InnerException.Message);
        }
    }

    /// <summary>The visitor chose a button of a dialog.</summary>
    public RouteableAck Answer(string id, int index)
        => _interactions.Answer(id, index) ? RouteableAck.Ok : RouteableAck.Fail("No such dialog is waiting for an answer.");

    /// <summary>The visitor closed a notification, or clicked it.</summary>
    public RouteableAck Dismiss(int id, bool click)
        => _interactions.Dismiss(id, click) ? RouteableAck.Ok : RouteableAck.Fail("No such notification.");

    private static RouteableAck NoViewModel(string target)
        => RouteableAck.Fail(target.Length == 0 ? "No page is current." : $"Nothing is shown in '{target}': watch it first.");

    private object? ViewModelOf(string target)
        => string.IsNullOrEmpty(target) ? _router.CurrentViewModel : _targets.TryGetValue(target, out var watched) ? watched.ViewModel : null;

    // A command that opens a dialog does not end until the visitor answers, and the answer is a later call of the same connection:
    // the call returns when the command ends, when it asks something, or after a moment (a countdown goes on by itself).
    private async Task<RouteableAck> RunAsync(Func<Task<RouteableAck>> work)
    {
        var asked = _interactions.Arm();
        var running = work();
        var first = await Task.WhenAny(running, asked, Task.Delay(Grace)).ConfigureAwait(false);
        if (first == running)
            return await running.ConfigureAwait(false);

        _ = running.ContinueWith(async task =>
        {
            var ack = task.IsFaulted ? RouteableAck.Fail(task.Exception!.GetBaseException().Message) : task.IsCompletedSuccessfully ? task.Result : null;
            if (ack is { Success: false })
                await SendNowAsync(() => _client.Failed(new RouteableFailure(string.Empty, ack.Error ?? "The command failed."))).ConfigureAwait(false);
        }, TaskScheduler.Default);
        return RouteableAck.Ok;
    }

    // ---------------------------------------------------------------- what the interactions tell

    private void OnDialog(RouteableDialog dialog) => Send(() => _client.DialogRequested(dialog));

    private void OnToast(RouteableToast toast) => Send(() => _client.Notified(toast));

    private void OnModalOpened(object viewModel)
    {
        if (_targets.TryRemove(RouteableTargets.Modal, out var previous))
            previous.Dispose();
        Put(RouteableTargets.Modal, new WatchedTarget(RouteableTargets.Modal, _client, Send, null, viewModel));
    }

    private void OnModalClosed()
    {
        if (_targets.TryRemove(RouteableTargets.Modal, out var modal))
            modal.Dispose();
        Send(() => _client.Closed(new RouteableClosed(RouteableTargets.Modal)));
    }

    private void Send(Func<Task> send) => _ = SendNowAsync(send);

    // A change is told from whichever thread raised it: a failure to tell (the client left) must not reach the view model.
    private async Task SendNowAsync(Func<Task> send)
    {
        if (_disposed)
            return;

        try { await send().ConfigureAwait(false); }
        catch (Exception) when (_disposed) { }
        catch (InvalidOperationException) { }
        catch (OperationCanceledException) { }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _interactions.DialogRequested -= OnDialog;
        _interactions.ToastRequested -= OnToast;
        _interactions.ModalOpened -= OnModalOpened;
        _interactions.ModalClosed -= OnModalClosed;
        foreach (var target in _targets.Values)
            target.Dispose();
        _targets.Clear();
        await _scope.DisposeAsync().ConfigureAwait(false);
    }
}
