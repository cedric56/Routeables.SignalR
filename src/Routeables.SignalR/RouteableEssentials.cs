using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Routeables.SignalR;

/// <summary>
/// The device APIs of the client of a session (its browser, its app): the server calls them (<see cref="InvokeAsync"/>), receives their events
/// (<see cref="Subscribe"/>) and reads the state the client announced (<see cref="TryGetState"/>). One per session, in its scope: the proxies of
/// Routeables.SignalR.Essentials (<c>IPreferences</c>, <c>IConnectivity</c>…) are built on it, but it does not know them.
/// </summary>
/// <remarks>
/// <para>
/// The client says what it supports with <see cref="EssentialsReady"/>, after <c>Hello</c> and again after each reconnection. Until then a call
/// waits (within its timeout). The events and <see cref="Ready"/> are raised on the dispatcher of the session, as the commands of the view models
/// run; a call that is awaited there comes back to it.
/// </para>
/// <para>
/// While the connection is gone, a call waits for the client to resume the session. When it comes back, a call it had received before is sent
/// again when it is replayable (<see cref="EssentialsCallOptions.Replay"/>) and fails as <see cref="EssentialsErrorCodes.Lost"/> otherwise; the
/// subscriptions are asked again. When the session ends, every call that waits fails as <see cref="EssentialsErrorCodes.Cancelled"/>.
/// </para>
/// <para>
/// What the client sends is not trusted: an answer to a call that does not wait in this session is dropped, the names are checked, the error codes
/// reduced to the known ones. A value the client reports (a location, a network state) is what the device says, never a proof.
/// </para>
/// </remarks>
public sealed class RouteableEssentials : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Service, string Event), List<Action<JsonElement?>>> _handlers = new();
    private readonly ILogger _logger;
    private Dictionary<string, JsonElement> _state = new(StringComparer.Ordinal);
    private HashSet<string> _capabilities = new(StringComparer.Ordinal);
    private TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private RouteableSession? _session;
    private long _nextCall;
    private long _epoch;   // grows at each EssentialsReady: a call sent before the last one went to a connection that is gone
    private bool _isReady;
    private bool _disposed;

    /// <summary>Creates the channel of a session (the session attaches itself).</summary>
    public RouteableEssentials(ILogger<RouteableEssentials>? logger = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    private sealed class Pending(EssentialsCall call, long sequence, bool replay)
    {
        public EssentialsCall Call { get; } = call;
        public long Sequence { get; } = sequence;
        public bool Replay { get; } = replay;
        public long Epoch { get; set; }
        public TaskCompletionSource<JsonElement> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal void Attach(RouteableSession session) => _session = session;

    /// <summary>Raised on the dispatcher of the session each time the client announced its services (the first time, and after a reconnection).</summary>
    public event Action? Ready;

    /// <summary>Whether the client is connected and said what it supports.</summary>
    public bool IsReady
    {
        get { lock (_gate) return _isReady; }
    }

    /// <summary>The services the client announced last.</summary>
    public IReadOnlyCollection<string> Capabilities
    {
        get { lock (_gate) return [.. _capabilities]; }
    }

    /// <summary>Whether the client announced <paramref name="service"/> (false before it said anything).</summary>
    public bool IsSupported(string service)
    {
        lock (_gate)
            return _capabilities.Contains(service);
    }

    /// <summary>Whether the client has announced its services at least once in this session.</summary>
    public bool HasAnnounced
    {
        get { lock (_gate) return _epoch > 0; }
    }

    /// <summary>The state of <paramref name="service"/> the client announced last.</summary>
    public bool TryGetState(string service, out JsonElement state)
    {
        lock (_gate)
            return _state.TryGetValue(service, out state);
    }

    /// <summary>Completes once the client is connected and said what it supports.</summary>
    public Task WhenReadyAsync(CancellationToken cancellationToken = default)
    {
        Task ready;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ready = _ready.Task;
        }
        return ready.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Calls <paramref name="method"/> of <paramref name="service"/> on the client and returns what it answered. Throws
    /// <see cref="RouteableEssentialsException"/> when it failed there (its <see cref="RouteableEssentialsException.Code"/>), when the client
    /// does not support the service, or did not answer within the timeout.
    /// </summary>
    public async Task<JsonElement> InvokeAsync(
        string service,
        string method,
        IReadOnlyList<object?>? args = null,
        EssentialsCallOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!EssentialsProtocol.IsName(service))
            throw new ArgumentException("Not a service name.", nameof(service));
        if (!EssentialsProtocol.IsName(method))
            throw new ArgumentException("Not a method name.", nameof(method));
        options ??= EssentialsCallOptions.Default;
        var session = _session ?? throw new InvalidOperationException("This channel belongs to no session.");

        var timeout = options.Timeout ?? session.Options.EssentialsTimeout;
        using var timeoutSource = timeout == Timeout.InfiniteTimeSpan ? null : new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.LifetimeToken, timeoutSource?.Token ?? CancellationToken.None);

        try
        {
            // The client says what it supports right after Hello; a call made before (or while the connection is gone) waits for it.
            await WhenReadyAsync(linked.Token).ConfigureAwait(true);
            if (!IsSupported(service))
                throw new RouteableEssentialsException(EssentialsErrorCodes.NotSupported, $"The client does not support '{service}'.");

            var sequence = Interlocked.Increment(ref _nextCall);
            var call = new EssentialsCall("e" + sequence.ToString(System.Globalization.CultureInfo.InvariantCulture), service, method, args ?? []);
            var pending = new Pending(call, sequence, options.Replay);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_pending.Count >= session.Options.EssentialsMaxPendingCalls)
                    throw new RouteableEssentialsException(EssentialsErrorCodes.Limit, $"At most {session.Options.EssentialsMaxPendingCalls} Essentials calls can wait for the client.");
                pending.Epoch = _epoch;
                _pending.Add(call.Id, pending);
            }

            try
            {
                // As for a dialog: the hub answers the command that waits ("pending"), so that the connection is free to carry the answer.
                if (options.Ask)
                    RouteableInvocation.Current?.Asked();
                Send(session, call);

                using (linked.Token.Register(static state => ((Pending)state!).Completion.TrySetCanceled(), pending))
                    return await pending.Completion.Task.ConfigureAwait(true);
            }
            finally
            {
                lock (_gate)
                    _pending.Remove(call.Id);
            }
        }
        catch (OperationCanceledException) when (timeoutSource?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested && !session.LifetimeToken.IsCancellationRequested)
        {
            throw new RouteableEssentialsException(
                IsReady ? EssentialsErrorCodes.Timeout : EssentialsErrorCodes.NotReady,
                IsReady
                    ? $"The client did not answer {service}.{method} within {timeout}."
                    : $"The client did not announce its Essentials services within {timeout} (EssentialsReady).");
        }
        catch (OperationCanceledException) when (session.LifetimeToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new RouteableEssentialsException(EssentialsErrorCodes.Cancelled, "The session ended.");
        }
    }

    /// <summary>
    /// Starts to receive <paramref name="eventName"/> of <paramref name="service"/>: the client is asked to start it with the first handler, and
    /// again after each reconnection; it is stopped with the last one. <paramref name="handler"/> runs on the dispatcher of the session.
    /// </summary>
    public IDisposable Subscribe(string service, string eventName, Action<JsonElement?> handler)
    {
        if (!EssentialsProtocol.IsName(service))
            throw new ArgumentException("Not a service name.", nameof(service));
        if (!EssentialsProtocol.IsName(eventName))
            throw new ArgumentException("Not an event name.", nameof(eventName));
        ArgumentNullException.ThrowIfNull(handler);

        bool first;
        bool send;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var key = (service, eventName);
            if (!_handlers.TryGetValue(key, out var handlers))
                _handlers[key] = handlers = [];
            first = handlers.Count == 0;
            handlers.Add(handler);
            send = first && _isReady && _capabilities.Contains(service);
        }
        if (send && _session is { } session)
            SendSubscription(session, new EssentialsSubscription(service, eventName, true));

        return new Subscription(this, service, eventName, handler);
    }

    private sealed class Subscription(RouteableEssentials owner, string service, string eventName, Action<JsonElement?> handler) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.Unsubscribe(service, eventName, handler);
        }
    }

    private void Unsubscribe(string service, string eventName, Action<JsonElement?> handler)
    {
        bool send;
        lock (_gate)
        {
            var key = (service, eventName);
            if (!_handlers.TryGetValue(key, out var handlers) || !handlers.Remove(handler) || handlers.Count > 0)
                return;
            _handlers.Remove(key);
            send = !_disposed && _isReady && _capabilities.Contains(service);
        }
        if (send && _session is { } session)
            SendSubscription(session, new EssentialsSubscription(service, eventName, false));
    }

    // ---------------------------------------------------------------- what the client tells

    /// <summary>The client announced its services (<see cref="EssentialsReady"/>).</summary>
    internal Task<RouteableAck> OnReadyAsync(EssentialsReady ready)
    {
        if (ready is null || ready.Version != EssentialsProtocol.Version)
            return Task.FromResult(RouteableAck.Fail(
                $"The client speaks the Essentials protocol {ready?.Version}, the server {EssentialsProtocol.Version}.", RouteableErrorCodes.Protocol));
        var capabilities = ready.Capabilities ?? [];
        if (capabilities.Count > EssentialsProtocol.MaxCapabilities || capabilities.Any(c => !EssentialsProtocol.IsName(c)))
            return Task.FromResult(RouteableAck.Fail("The Essentials services are not valid names, or too many.", RouteableErrorCodes.InvalidArgument));

        var session = _session;
        if (session is null)
            return Task.FromResult(RouteableAck.Fail("This channel belongs to no session.", RouteableErrorCodes.NotFound));

        return session.Dispatcher.InvokeAsync(() =>
        {
            List<Pending> replay = [];
            List<Pending> lost = [];
            List<EssentialsSubscription> subscriptions = [];
            TaskCompletionSource ready2;
            lock (_gate)
            {
                if (_disposed)
                    return RouteableAck.Fail("The session is closed.", RouteableErrorCodes.NotFound);

                _capabilities = new HashSet<string>(capabilities, StringComparer.Ordinal);
                _state = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var (service, state) in ready.State ?? new Dictionary<string, JsonElement>())
                    if (_capabilities.Contains(service))
                        _state[service] = state.Clone();

                var previous = _epoch++;
                foreach (var pending in _pending.Values)
                {
                    if (pending.Epoch > previous || pending.Completion.Task.IsCompleted)
                        continue;
                    if (pending.Replay && _capabilities.Contains(pending.Call.Service))
                    {
                        pending.Epoch = _epoch;
                        replay.Add(pending);
                    }
                    else
                        lost.Add(pending);
                }
                foreach (var ((service, eventName), handlers) in _handlers)
                    if (handlers.Count > 0 && _capabilities.Contains(service))
                        subscriptions.Add(new EssentialsSubscription(service, eventName, true));

                _isReady = true;
                ready2 = _ready;
            }

            foreach (var pending in lost)
                pending.Completion.TrySetException(new RouteableEssentialsException(EssentialsErrorCodes.Lost,
                    $"The connection dropped before the client answered {pending.Call.Service}.{pending.Call.Method}: it may have run."));

            // The state first (the proxies read it), then what was waiting: subscriptions, calls sent again, calls made before the client was ready.
            try { Ready?.Invoke(); }
            catch (Exception exception) { _logger.LogError(exception, "A handler of the Essentials Ready event failed."); }
            foreach (var subscription in subscriptions)
                SendSubscription(session, subscription);
            foreach (var pending in replay.OrderBy(p => p.Sequence))   // in the order they were made: writes must not be reordered
                Send(session, pending.Call);
            ready2.TrySetResult();
            return RouteableAck.Ok;
        });
    }

    /// <summary>The client answered a call.</summary>
    internal void OnResult(EssentialsResult result)
    {
        if (result is null || string.IsNullOrEmpty(result.Id) || result.Id.Length > 32)
            return;
        Pending? pending;
        lock (_gate)
        {
            if (!_pending.TryGetValue(result.Id, out pending))
            {
                // Late (after a timeout), answered twice, or made up: nothing waits for it.
                _logger.LogDebug("An Essentials answer was dropped: no call {Id} waits for it.", result.Id);
                return;
            }
        }

        if (result.Success)
            pending.Completion.TrySetResult(result.Result?.Clone() ?? default);
        else
        {
            var error = string.IsNullOrWhiteSpace(result.Error) ? "The client could not do it." : result.Error;
            if (error.Length > 1024)
                error = error[..1024];
            pending.Completion.TrySetException(new RouteableEssentialsException(EssentialsErrorCodes.Normalize(result.Code), error));
        }
    }

    /// <summary>The client told an event.</summary>
    internal void OnEvent(EssentialsEvent message)
    {
        if (message is null || !EssentialsProtocol.IsName(message.Service) || !EssentialsProtocol.IsName(message.Event))
            return;
        Action<JsonElement?>[] handlers;
        lock (_gate)
        {
            if (_disposed || !_handlers.TryGetValue((message.Service, message.Event), out var list) || list.Count == 0)
                return;   // nobody asked for it
            handlers = [.. list];
        }

        var payload = message.Payload?.Clone();
        _session?.Dispatcher.Run(() =>
        {
            foreach (var handler in handlers)
            {
                try { handler(payload); }
                catch (Exception exception) { _logger.LogError(exception, "A handler of the Essentials event {Service}.{Event} failed.", message.Service, message.Event); }
            }
        });
    }

    /// <summary>The connection dropped: calls wait for the client to come back (or fail at their timeout).</summary>
    internal void OnDetached()
    {
        lock (_gate)
        {
            if (!_isReady)
                return;
            _isReady = false;
            _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private static void Send(RouteableSession session, EssentialsCall call)
        => session.Outbound.Essentials(client => client.EssentialsInvoke(call));

    private static void SendSubscription(RouteableSession session, EssentialsSubscription subscription)
        => session.Outbound.Essentials(client => client.EssentialsSubscribe(subscription));

    /// <summary>Ends every call that waits (<see cref="EssentialsErrorCodes.Cancelled"/>) and drops the subscriptions.</summary>
    public void Dispose()
    {
        List<Pending> pending;
        TaskCompletionSource ready;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            pending = [.. _pending.Values];
            _pending.Clear();
            _handlers.Clear();
            ready = _ready;
        }
        foreach (var call in pending)
            call.Completion.TrySetException(new RouteableEssentialsException(EssentialsErrorCodes.Cancelled, "The session ended."));
        ready.TrySetCanceled();
    }
}
