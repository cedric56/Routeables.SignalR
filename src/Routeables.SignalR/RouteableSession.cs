using System.Diagnostics;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Routeables.Abstraction;

namespace Routeables.SignalR;

/// <summary>
/// What a connection owns: its routers, with their histories, and the view models they created. The connection is the lifetime, as the circuit is
/// for Blazor Server: when it closes for good, the session is disposed and the view models with it. When it drops, the session waits
/// <see cref="RouteablesOptions.ReconnectGracePeriod"/> for the client to resume it.
/// The client sees <em>targets</em> (see <see cref="RouteableTargets"/>): the page of the main router is always watched; the regions and the
/// registered view models are watched when the client asks (<see cref="WatchAsync"/>).
/// </summary>
/// <remarks>
/// Every call and every change runs on the dispatcher of the session, one at a time (an <c>await</c> in a view model comes back to it). The answer
/// to a call is given once the messages the call caused were written: a client that gets the answer already has the state that goes with it.
/// A call that lasts longer than <see cref="RouteablesOptions.CommandGrace"/>, or that asks the visitor something, is answered
/// <see cref="RouteableAck.Pending"/> and ends with <see cref="IRouteableClient.Completed"/>.
/// </remarks>
public sealed class RouteableSession : IAsyncDisposable
{
    private readonly AsyncServiceScope _scope;
    private readonly IRouter _router;
    private readonly RemoteInteractions _interactions;
    private readonly RouteableCommandInvoker _invoker;
    private readonly RouteableConnection _connection;
    private readonly IReadOnlyDictionary<string, Type> _registered;
    private readonly Dictionary<string, WatchedTarget> _targets = new(StringComparer.Ordinal);
    private readonly HashSet<WatchedTarget> _pendingTargets = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ILogger _logger;
    private readonly Action? _abort;
    private readonly Dictionary<string, RouteableCompleted?> _operations = new(StringComparer.Ordinal);
    private readonly Queue<string> _ended = new();
    private long _nextOperation;
    private int _disposed;

    // The calls that went on: null while they run, then their end, kept for the last ones to tell it again to a client that resumes the session.
    private const int RememberedOperations = 256;

    /// <summary>Creates the session of one connection.</summary>
    /// <param name="services">The application services; the session opens its own scope.</param>
    /// <param name="client">Where the session sends what changes.</param>
    /// <param name="user">The user of the connection.</param>
    /// <param name="connectionId">The connection.</param>
    /// <param name="abort">Closes the connection (a client too slow to follow).</param>
    public RouteableSession(IServiceProvider services, IRouteableClient client, ClaimsPrincipal? user = null, string? connectionId = null, Action? abort = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(client);
        _abort = abort;
        _scope = services.CreateAsyncScope();
        var provider = _scope.ServiceProvider;

        Options = provider.GetService<RouteablesOptions>() ?? new RouteablesOptions();
        Schema = provider.GetService<RouteableSchema>() ?? new RouteableSchema(Options);
        Projector = new RouteableStateProjector(Schema);
        var detailed = Options.DetailedErrors ?? provider.GetService<IHostEnvironment>()?.IsDevelopment() ?? false;
        _invoker = new RouteableCommandInvoker(Schema, detailed);
        _logger = provider.GetService<ILoggerFactory>()?.CreateLogger<RouteableSession>() ?? NullLogger<RouteableSession>.Instance;

        _connection = provider.GetService<RouteableConnection>() ?? new RouteableConnection();
        _connection.Attach(connectionId, user);
        Authorizer = new RouteableAuthorizer(provider, _connection);
        UserKey = KeyOf(user);
        Token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Dispatcher = new SessionDispatcher(EndOfTurn, Unhandled);
        Outbound = new Outbound(GenerationOf, Abort, Math.Max(1, Options.MaxPendingMessages), _logger);
        Outbound.Attach(client);

        _router = provider.GetRequiredService<IRouter>();
        _registered = provider.GetServices<RouteableTarget>().ToDictionary(t => t.Name, t => t.ViewModel, StringComparer.Ordinal);
        _interactions = provider.GetRequiredService<RemoteInteractions>();
        _interactions.DialogRequested += OnDialog;
        _interactions.ToastRequested += OnToast;
        _interactions.ModalOpened += OnModalOpened;
        _interactions.ModalClosed += OnModalClosed;

        // The page of the main router is told to the client whoever asked for it: the client (GoTo) or a command of a view model that navigates.
        Dispatcher.InvokeAsync(() => Put(RouteableTargets.Page, new WatchedTarget(RouteableTargets.Page, this, _router))).GetAwaiter().GetResult();
    }

    /// <summary>The token that resumes this session from another connection (<see cref="RouteableWelcome.Session"/>).</summary>
    public string Token { get; }

    /// <summary>The view model of the page that is current, or <see langword="null"/>.</summary>
    public object? Current => _router.CurrentViewModel;

    /// <summary>The services of the session (its scope).</summary>
    public IServiceProvider Services => _scope.ServiceProvider;

    /// <summary>Whether the session was disposed.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    internal RouteablesOptions Options { get; }
    internal RouteableSchema Schema { get; }
    internal RouteableStateProjector Projector { get; }
    internal RouteableAuthorizer Authorizer { get; }
    internal SessionDispatcher Dispatcher { get; }
    internal Outbound Outbound { get; }
    /// <summary>Who may resume the session: <see langword="null"/> when nobody may (a user authenticated without a stable identifier).</summary>
    internal string? UserKey { get; }

    /// <summary>The user of the connection the session is attached to.</summary>
    public ClaimsPrincipal? User => _connection.User;

    /// <summary>Whether the client said <c>Hello</c>: only such a session waits for its client when the connection drops.</summary>
    internal bool Greeted { get; set; }

    // ---------------------------------------------------------------- targets

    /// <summary>Starts to tell the client what a target shows: a region, or a view model registered with <c>AddRouteableTarget</c>.</summary>
    public Task<RouteableAck> WatchAsync(string target) => CallAsync("Watch", target, () => Task.FromResult(Watch(target)), grace: false);

    private RouteableAck Watch(string target)
    {
        if (string.IsNullOrEmpty(target) || target == RouteableTargets.Modal)
            return RouteableAck.Ok;   // the page and the modal are always watched
        if (target.Length > 256)
            return RouteableAck.Fail("The name of the target is too long.", RouteableErrorCodes.InvalidArgument);

        if (_targets.TryGetValue(target, out var watched))
        {
            // Asked again (a client that reconnected, a component that came back): the state is sent again.
            watched.Resend();
            return RouteableAck.Ok;
        }

        // The page and the modal do not count: the limit is about what the client may add. The dispatcher makes the check and the add one step.
        if (_targets.Keys.Count(k => k != RouteableTargets.Page && k != RouteableTargets.Modal) >= Options.MaxTargets)
            return RouteableAck.Fail($"At most {Options.MaxTargets} targets can be watched.", RouteableErrorCodes.Limit);

        if (target.StartsWith('$'))
        {
            if (!_registered.TryGetValue(target, out var type))
                return RouteableAck.Fail($"No target is registered under '{target}'.", RouteableErrorCodes.NotFound);
            Put(target, new WatchedTarget(target, this, null, _scope.ServiceProvider.GetRequiredService(type)));
        }
        else
        {
            var router = FindRouter(target, out var error);
            if (router is null)
                return RouteableAck.Fail(error!, RouteableErrorCodes.NotFound);
            Put(target, new WatchedTarget(target, this, router));
        }
        return RouteableAck.Ok;
    }

    /// <summary>Stops telling the client about a target (the main page and the modal stay).</summary>
    public Task<RouteableAck> UnwatchAsync(string target) => CallAsync("Unwatch", target, () =>
    {
        if (!string.IsNullOrEmpty(target) && target != RouteableTargets.Modal && RemoveTarget(target, out var watched))
            watched.Dispose();
        return Task.FromResult(RouteableAck.Ok);
    }, grace: false);

    /// <summary>Sends the full state of a target again: the client lost a change (a version is missing) or does not trust what it has.</summary>
    public Task<RouteableAck> ResyncAsync(string target) => CallAsync("Resync", target, () =>
    {
        if (!_targets.TryGetValue(target ?? string.Empty, out var watched))
            return Task.FromResult(RouteableAck.Fail($"'{target}' is not watched.", RouteableErrorCodes.NotFound));
        watched.Resend();
        return Task.FromResult(RouteableAck.Ok);
    }, grace: false);

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
        if (RemoveTarget(name, out var previous))
            previous.Dispose();
        lock (_targets)
            _targets[name] = target;
        target.Start();
    }

    // Read from the thread that sends. The targets change on the dispatcher only, under the lock; a target that is gone makes its messages stale.
    private long GenerationOf(string target)
    {
        lock (_targets)
            return _targets.TryGetValue(target, out var watched) ? watched.Generation : long.MaxValue;
    }

    private bool RemoveTarget(string name, out WatchedTarget target)
    {
        lock (_targets)
            return _targets.Remove(name, out target!);
    }

    // ---------------------------------------------------------------- what the client asks

    /// <summary>Goes to <paramref name="route"/> in the main router. The client is told by <see cref="IRouteableClient.Navigated"/> when it succeeds.</summary>
    public Task<RouteableAck> GoToAsync(string route, CancellationToken cancellationToken = default)
        => GoToInAsync(RouteableTargets.Page, route, cancellationToken);

    /// <summary>
    /// Goes to <paramref name="route"/> in the router of a target (the main router, or a region). A route change may ask the visitor something
    /// (a page that asks whether to leave): it is answered as a command is, without waiting for the answer.
    /// </summary>
    public Task<RouteableAck> GoToInAsync(string target, string route, CancellationToken cancellationToken = default)
        => CallAsync("GoTo", target, async () =>
        {
            if (string.IsNullOrWhiteSpace(route))
                return RouteableAck.Fail("The route is empty.", RouteableErrorCodes.InvalidArgument);
            if (route.Length > 2048)
                return RouteableAck.Fail("The route is too long.", RouteableErrorCodes.InvalidArgument);

            var router = FindRouter(target, out var error);
            if (router is null)
                return RouteableAck.Fail(error!, RouteableErrorCodes.NotFound);

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            var result = await router.GoToAsync(route, cancellationToken: linked.Token).ConfigureAwait(true);
            if (result.Success)
                return RouteableAck.Ok;

            var message = result.Exception is { } exception
                ? (_invoker.DetailedErrors || exception is RouteableException ? exception.Message : $"The route '{route}' could not be opened.")
                : $"The route '{route}' could not be opened.";
            if (result.Exception is { } unexpected and not RouteableException)
                _logger.LogWarning(unexpected, "The route {Route} could not be opened.", route);
            Report(new RouteableFailure(route, message, target, RouteableErrorCodes.Failed));
            return RouteableAck.Fail(message);
        }, grace: true);

    /// <summary>Calls a command of the current page.</summary>
    public Task<RouteableAck> InvokeAsync(string command, IReadOnlyList<JsonElement>? arguments)
        => InvokeOnAsync(RouteableTargets.Page, command, arguments);

    /// <summary>Calls a command of the view model that a target shows.</summary>
    public Task<RouteableAck> InvokeOnAsync(string target, string command, IReadOnlyList<JsonElement>? arguments)
        => CallAsync("Invoke", target, async () =>
        {
            if (ViewModelOf(target) is not { } viewModel)
                return NoViewModel(target);
            var outcome = await _invoker.InvokeAsync(viewModel, command, arguments, Authorizer, _lifetime.Token).ConfigureAwait(true);
            if (outcome.Unexpected is { } exception)
                _logger.LogError(exception, "The command {Command} of {ViewModel} failed.", command, viewModel.GetType().Name);
            return outcome.Ack;
        }, grace: true);

    /// <summary>Sets a property of the current page (what a text box or a check box does).</summary>
    public Task<RouteableAck> SetAsync(string property, JsonElement value) => SetOnAsync(RouteableTargets.Page, property, value);

    /// <summary>Sets a property of the view model that a target shows: one the schema lets the client set.</summary>
    public Task<RouteableAck> SetOnAsync(string target, string property, JsonElement value)
        => CallAsync("Set", target, async () =>
        {
            if (ViewModelOf(target) is not { } viewModel)
                return NoViewModel(target);
            var outcome = await _invoker.SetAsync(viewModel, property, value, Authorizer).ConfigureAwait(true);
            if (outcome.Unexpected is { } exception)
                _logger.LogError(exception, "Setting {Property} of {ViewModel} failed.", property, viewModel.GetType().Name);
            return outcome.Ack;
        }, grace: false);

    /// <summary>The visitor chose a button of a dialog.</summary>
    public Task<RouteableAck> AnswerAsync(string id, int index) => CallAsync("Answer", string.Empty, () => Task.FromResult(
        _interactions.Answer(id, index) ? RouteableAck.Ok
        : _interactions.IsWaiting(id) ? RouteableAck.Fail($"The dialog has no button {index}.", RouteableErrorCodes.InvalidArgument)
        : RouteableAck.Fail("No such dialog is waiting for an answer.", RouteableErrorCodes.NotFound)), grace: false);

    /// <summary>The visitor closed a notification, or clicked it.</summary>
    public Task<RouteableAck> DismissAsync(int id, bool click) => CallAsync("Dismiss", string.Empty, () => Task.FromResult(
        _interactions.Dismiss(id, click) ? RouteableAck.Ok : RouteableAck.Fail("No such notification.", RouteableErrorCodes.NotFound)), grace: false);

    /// <summary>Completes once what was asked so far has run and the messages it caused have been written.</summary>
    public async Task FlushAsync()
    {
        await Dispatcher.IdleAsync().ConfigureAwait(false);
        await Outbound.DrainedAsync().WaitAsync(_lifetime.Token).ConfigureAwait(false);
    }

    private static RouteableAck NoViewModel(string target)
        => RouteableAck.Fail(target.Length == 0 ? "No page is current." : $"Nothing is shown in '{target}': watch it first.", RouteableErrorCodes.NotFound);

    private object? ViewModelOf(string target)
        => _targets.TryGetValue(target ?? string.Empty, out var watched) ? watched.ViewModel : null;

    // Every call: on the dispatcher, traced and measured. A call with grace is answered when it ends, when it asks the visitor something, or after
    // the grace; then it goes on and Completed tells its end. The answer is given once the messages the call caused were written.
    private async Task<RouteableAck> CallAsync(string method, string? target, Func<Task<RouteableAck>> work, bool grace)
    {
        if (IsDisposed)
            return RouteableAck.Fail("The session is closed.", RouteableErrorCodes.NotFound);

        using var activity = RouteablesTelemetry.Source.StartActivity("Routeables " + method);
        activity?.SetTag("routeables.target", target);
        var started = Stopwatch.GetTimestamp();
        var invocation = new RouteableInvocation();

        var running = Dispatcher.InvokeAsync(() =>
        {
            RouteableInvocation.Current = invocation;
            return work();
        });

        RouteableAck ack;
        try
        {
            if (grace && !running.IsCompleted)
            {
                using var delay = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                var first = await Task.WhenAny(running, invocation.AskedTask, Task.Delay(Options.CommandGrace, delay.Token)).ConfigureAwait(false);
                delay.Cancel();
                ack = first == running ? await Settle(running).ConfigureAwait(false) : GoOn(method, running);
            }
            else
                ack = await Settle(running).ConfigureAwait(false);

            // The answer comes after the messages the call caused. A call that goes on may hold the dispatcher (a long synchronous command):
            // only what it already told is waited for then.
            if (ack.Pending)
                await Outbound.DrainedAsync().WaitAsync(_lifetime.Token).ConfigureAwait(false);
            else
                await FlushAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (IsDisposed)
        {
            ack = RouteableAck.Fail("The session is closed.", RouteableErrorCodes.Cancelled);
        }

        activity?.SetTag("routeables.outcome", ack.Pending ? "pending" : ack.Code ?? "ok");
        var tags = new TagList { { "method", method }, { "outcome", ack.Pending ? "pending" : ack.Code ?? "ok" } };
        RouteablesTelemetry.Calls.Add(1, tags);
        RouteablesTelemetry.CallDuration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, tags);
        return ack;
    }

    private async Task<RouteableAck> Settle(Task<RouteableAck> running)
    {
        try { return await running.ConfigureAwait(false); }
        catch (OperationCanceledException) { return RouteableAck.Fail("The call was cancelled.", RouteableErrorCodes.Cancelled); }
        catch (Exception exception)
        {
            _logger.LogError(exception, "A call of the client failed.");
            return RouteableAck.Fail(_invoker.DetailedErrors ? exception.Message : "The call failed.");
        }
    }

    // The call goes on: its end is told by Completed, after the messages it caused (posted to the dispatcher, behind its last turn).
    private RouteableAck GoOn(string method, Task<RouteableAck> running)
    {
        var operation = "op" + Interlocked.Increment(ref _nextOperation).ToString(System.Globalization.CultureInfo.InvariantCulture);
        lock (_operations)
            _operations[operation] = null;
        _ = running.ContinueWith(async task =>
        {
            var ack = await Settle(task).ConfigureAwait(false);
            if (IsDisposed)
                return;
            Dispatcher.Post(_ =>
            {
                FlushTurn();
                var completed = new RouteableCompleted(operation, ack.Success, ack.Error, ack.Code);
                lock (_operations)
                {
                    _operations[operation] = completed;
                    _ended.Enqueue(operation);
                    while (_ended.Count > RememberedOperations)
                        _operations.Remove(_ended.Dequeue());
                }
                Outbound.Completed(completed);
            }, null);
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        _logger.LogDebug("The call {Method} goes on as {Operation}.", method, operation);
        return RouteableAck.Running(operation);
    }

    // ---------------------------------------------------------------- what the interactions tell

    private void OnDialog(RouteableDialog dialog) => Dispatcher.Run(() => { FlushTurn(); Outbound.Dialog(dialog); });

    private void OnToast(RouteableToast toast) => Dispatcher.Run(() => { FlushTurn(); Outbound.Toast(toast); });

    // The modals open, the last one on top: the client shows the one on top. A modal opened from another one covers it; when it closes, the
    // one under it is shown again (a new generation of the target), still awaited by the command that opened it.
    private readonly List<object> _modals = [];

    private void OnModalOpened(object viewModel) => Dispatcher.Run(() =>
    {
        _modals.Add(viewModel);
        Put(RouteableTargets.Modal, new WatchedTarget(RouteableTargets.Modal, this, null, viewModel));
    });

    private void OnModalClosed(object viewModel) => Dispatcher.Run(() =>
    {
        var index = _modals.FindLastIndex(m => ReferenceEquals(m, viewModel));
        if (index < 0)
            return;
        _modals.RemoveAt(index);
        if (index < _modals.Count)
            return;   // a modal under the one shown closed: the one shown stays

        if (_modals.Count > 0)
        {
            Put(RouteableTargets.Modal, new WatchedTarget(RouteableTargets.Modal, this, null, _modals[^1]));
            return;
        }
        if (!RemoveTarget(RouteableTargets.Modal, out var modal))
            return;
        var generation = modal.Generation;
        modal.Dispose();
        FlushTurn();
        Outbound.Closed(new RouteableClosed(RouteableTargets.Modal, generation));
    });

    // ---------------------------------------------------------------- turns

    internal void MarkPending(WatchedTarget target) => _pendingTargets.Add(target);

    /// <summary>Called before a message that is not a change: the changes made before it are told first.</summary>
    internal void BeforeMessage() => FlushTurn();

    // The end of a turn of the dispatcher: what changed during it is told, one message per target.
    private void EndOfTurn() => FlushTurn();

    private void FlushTurn()
    {
        if (_pendingTargets.Count == 0)
            return;
        var targets = _pendingTargets.ToList();
        _pendingTargets.Clear();
        foreach (var target in targets)
            target.Flush();
    }

    internal void Report(RouteableFailure failure)
    {
        _logger.LogWarning("{Target}: {Message}", failure.Target, failure.Message);
        if (Dispatcher.CheckAccess())
            FlushTurn();
        Outbound.Failed(failure);
    }

    internal void Unhandled(Exception exception) => _logger.LogError(exception, "An exception was not observed in a session.");

    internal bool DetailedErrors => _invoker.DetailedErrors;

    internal void LogProjectionFailure(Exception exception, string target, string property)
        => _logger.LogError(exception, "The property {Property} of the target '{Target}' could not be read.", property, target);

    // ---------------------------------------------------------------- reconnection

    /// <summary>
    /// Who a user is, to resume a session: the empty string for an anonymous user (the token is the only key), the scheme, the issuer and the
    /// identifier of an authenticated one (<c>NameIdentifier</c>, else <c>sub</c>), and <see langword="null"/> when an authenticated user has no
    /// identifier: such a session cannot be resumed, rather than be confused with the session of another user without one.
    /// </summary>
    internal static string? KeyOf(ClaimsPrincipal? user)
    {
        var identity = user?.Identity;
        if (identity is not { IsAuthenticated: true })
            return string.Empty;
        var claim = user!.FindFirst(ClaimTypes.NameIdentifier) ?? user.FindFirst("sub");
        if (claim is null || string.IsNullOrWhiteSpace(claim.Value))
            return null;
        var scheme = identity.AuthenticationType ?? string.Empty;
        return $"{scheme.Length}:{scheme}|{claim.Issuer.Length}:{claim.Issuer}|{claim.Value}";
    }

    /// <summary>
    /// Evaluates again what the user may see and sends every target again: a view model the user may not see any more is closed, a property
    /// the user may not read any more is no longer sent (and the reverse). <see cref="RouteableSessions.ReauthorizeAsync"/> does it for many sessions.
    /// </summary>
    public Task ReauthorizeAsync()
    {
        if (IsDisposed)
            return Task.CompletedTask;
        return Dispatcher.InvokeAsync(() =>
        {
            foreach (var target in _targets.Values.ToList())
                target.Resend();
        });
    }

    /// <summary>The connection dropped: the session keeps its view models and waits for the client.</summary>
    internal void Detach()
    {
        Outbound.Attach(null);
        _connection.Attach(null, _connection.User);
    }

    /// <summary>The client came back on another connection: the state of every target and the dialogs that wait are sent again.</summary>
    internal Task AttachAsync(IRouteableClient client, string connectionId, ClaimsPrincipal? user, Action? abort)
        => Dispatcher.InvokeAsync(() =>
        {
            _connection.Attach(connectionId, user);
            _abortOverride = abort;
            Outbound.Attach(client);
            foreach (var target in _targets.Values.ToList())
                target.Resend();
            foreach (var dialog in _interactions.PendingDialogs)
                Outbound.Dialog(dialog);
        });

    /// <summary>
    /// The client resumed the session and still waits for the end of <paramref name="waiting"/>: the end of each one that ended is told again, after
    /// the state sent again; one that still runs ends later; one the session forgot ends as <see cref="RouteableErrorCodes.Lost"/>.
    /// </summary>
    internal Task TellEndsAsync(IReadOnlyList<string>? waiting)
    {
        if (waiting is null || waiting.Count == 0)
            return Task.CompletedTask;
        return Dispatcher.InvokeAsync(() =>
        {
            FlushTurn();
            foreach (var operation in waiting.Take(RememberedOperations * 4).Distinct(StringComparer.Ordinal))
            {
                if (string.IsNullOrEmpty(operation) || operation.Length > 64)
                    continue;
                RouteableCompleted? completed;
                bool known;
                lock (_operations)
                    known = _operations.TryGetValue(operation, out completed);
                if (known && completed is null)
                    continue;   // it still runs
                Outbound.Completed(completed ?? new RouteableCompleted(operation, false, "The end of this call is not known any more: it may have run.", RouteableErrorCodes.Lost));
            }
        });
    }

    // The connection the session is attached to now: the one it was created for, or the one that resumed it.
    private Action? _abortOverride;

    internal void Abort() => (_abortOverride ?? _abort)?.Invoke();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await Dispatcher.InvokeAsync(() =>
            {
                _interactions.DialogRequested -= OnDialog;
                _interactions.ToastRequested -= OnToast;
                _interactions.ModalOpened -= OnModalOpened;
                _interactions.ModalClosed -= OnModalClosed;
                foreach (var target in _targets.Values)
                    target.Dispose();
                lock (_targets)
                    _targets.Clear();
                _pendingTargets.Clear();
            }).ConfigureAwait(false);
        }
        finally
        {
            Outbound.Dispose();
            await _scope.DisposeAsync().ConfigureAwait(false);
            // _lifetime is cancelled, not disposed: a call that ends after the session may still read its token.
        }
    }
}
