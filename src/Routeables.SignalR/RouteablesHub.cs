using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Routeables.SignalR;

/// <summary>
/// The hub a browser client talks to: one <see cref="RouteableSession"/> per connection. The client says <c>Hello</c> first (and again after a
/// reconnection, to resume its session), then asks (<c>GoTo</c>, <c>Invoke</c>, <c>Set</c>); the server tells through <see cref="IRouteableClient"/>.
/// </summary>
/// <remarks>
/// Protect the endpoint as any other: <c>app.MapRouteables().RequireAuthorization()</c>. The commands, the properties and the view models are
/// authorized one by one (<c>[Authorize]</c>), at every call.
/// </remarks>
public class RouteablesHub(IServiceProvider services, RouteableSessions sessions) : Hub<IRouteableClient>
{
    private readonly RouteablesOptions _options = services.GetService<RouteablesOptions>() ?? new RouteablesOptions();

    /// <inheritdoc />
    public override Task OnConnectedAsync()
    {
        if (_options.RequireAuthenticatedUser && Context.User?.Identity?.IsAuthenticated != true)
        {
            Context.Abort();
            return Task.CompletedTask;
        }

        var context = Context;
        sessions.Add(Context.ConnectionId, new RouteableSession(services, Clients.Caller, Context.User, Context.ConnectionId, context.Abort));
        return base.OnConnectedAsync();
    }

    /// <inheritdoc />
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await sessions.DisconnectAsync(Context.ConnectionId).ConfigureAwait(false);
        await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
    }

    /// <summary>
    /// What the client says first, and again after a reconnection: the protocol it speaks and, after a reconnection, the token of its session.
    /// When the session is resumed, its targets and the dialogs that wait are sent again.
    /// </summary>
    public async Task<RouteableWelcome> Hello(RouteableHello hello)
    {
        ArgumentNullException.ThrowIfNull(hello);
        if (hello.Protocol != RouteableProtocol.Version)
            return new RouteableWelcome(false, RouteableProtocol.Version, null, false,
                $"The client speaks the protocol {hello.Protocol}, the server {RouteableProtocol.Version}: update @routeables/client.");

        var current = Session;
        current.Greeted = true;   // a client that never says Hello is not waited for when its connection drops
        if (!string.IsNullOrEmpty(hello.Resume) && hello.Resume != current.Token)
        {
            // The calls that arrive meanwhile (HubOptions.MaximumParallelInvocationsPerClient above 1) wait: they belong to the session
            // that is resumed, not to the one the connection opened.
            using var greeting = sessions.BeginGreeting(Context.ConnectionId);
            var context = Context;
            var resumed = await sessions.ResumeAsync(hello.Resume, Context.ConnectionId, Clients.Caller, Context.User, context.Abort).ConfigureAwait(false);
            if (resumed is not null)
            {
                await resumed.TellEndsAsync(hello.Waiting).ConfigureAwait(false);
                await resumed.FlushAsync().ConfigureAwait(false);
                return new RouteableWelcome(true, RouteableProtocol.Version, resumed.Token, true);
            }
        }
        return new RouteableWelcome(true, RouteableProtocol.Version, current.Token, false);
    }

    /// <summary>Goes to a route, as the address bar of a browser would.</summary>
    public async Task<RouteableAck> GoTo(string route)
        => await (await SessionAsync()).GoToAsync(route, Context.ConnectionAborted).ConfigureAwait(false);

    /// <summary>
    /// Calls a command of the current view model. SignalR does not support optional parameters: the client always sends the
    /// arguments, an empty array when the command has none.
    /// </summary>
    public async Task<RouteableAck> Invoke(string command, JsonElement[]? arguments)
        => await (await SessionAsync()).InvokeAsync(command, arguments).ConfigureAwait(false);

    /// <summary>Sets a property of the current view model.</summary>
    public async Task<RouteableAck> Set(string property, JsonElement value)
        => await (await SessionAsync()).SetAsync(property, value).ConfigureAwait(false);

    /// <summary>Starts to follow a region or a registered view model (<c>$name</c>): the client is told what it shows and what changes.</summary>
    public async Task<RouteableAck> Watch(string target) => await (await SessionAsync()).WatchAsync(target).ConfigureAwait(false);

    /// <summary>Stops following a target.</summary>
    public async Task<RouteableAck> Unwatch(string target) => await (await SessionAsync()).UnwatchAsync(target).ConfigureAwait(false);

    /// <summary>Sends the full state of a target again (a change was lost).</summary>
    public async Task<RouteableAck> Resync(string target) => await (await SessionAsync()).ResyncAsync(target).ConfigureAwait(false);

    /// <summary>Goes to a route in the router of a target (the main page is the empty target, a region its name).</summary>
    public async Task<RouteableAck> GoToIn(string target, string route)
        => await (await SessionAsync()).GoToInAsync(target, route, Context.ConnectionAborted).ConfigureAwait(false);

    /// <summary>Calls a command of the view model a target shows: a method that says so, or a command of its state by path.</summary>
    public async Task<RouteableAck> InvokeOn(string target, string command, JsonElement[]? arguments)
        => await (await SessionAsync()).InvokeOnAsync(target, command, arguments).ConfigureAwait(false);

    /// <summary>Sets a property of the view model a target shows.</summary>
    public async Task<RouteableAck> SetOn(string target, string property, JsonElement value)
        => await (await SessionAsync()).SetOnAsync(target, property, value).ConfigureAwait(false);

    /// <summary>Answers a dialog: the index of the button that was chosen.</summary>
    public async Task<RouteableAck> Answer(string id, int button) => await (await SessionAsync()).AnswerAsync(id, button).ConfigureAwait(false);

    /// <summary>A notification was closed (or clicked when <paramref name="click"/> is true).</summary>
    public async Task<RouteableAck> Dismiss(int id, bool click) => await (await SessionAsync()).DismissAsync(id, click).ConfigureAwait(false);

    /// <summary>
    /// The client says which Essentials services it implements, and their state. Call it after <c>Hello</c> (on the session that was resumed),
    /// and again after each reconnection.
    /// </summary>
    public async Task<RouteableAck> EssentialsReady(EssentialsReady ready)
        => await (await SessionAsync()).Essentials.OnReadyAsync(ready).ConfigureAwait(false);

    /// <summary>The answer of the client to an Essentials call.</summary>
    public async Task EssentialsResult(EssentialsResult result) => (await SessionAsync()).Essentials.OnResult(result);

    /// <summary>An event of a device API of the client, for a subscription the server asked.</summary>
    public async Task EssentialsEvent(EssentialsEvent message) => (await SessionAsync()).Essentials.OnEvent(message);

    private RouteableSession Session
        => sessions.Get(Context.ConnectionId) ?? throw new HubException("The session of this connection is closed.");

    // The session of the connection, once a Hello that resumes another one has ended.
    private async ValueTask<RouteableSession> SessionAsync()
    {
        if (sessions.GreetingOf(Context.ConnectionId) is { } greeting)
            await greeting.ConfigureAwait(false);
        return Session;
    }
}

/// <summary>
/// The sessions of the open connections, and those whose connection dropped less than <see cref="RouteablesOptions.ReconnectGracePeriod"/> ago,
/// waiting for their client to resume them (<c>Hello</c> with their token). A session is resumed only by the user it was created for.
/// </summary>
/// <remarks>
/// A session lives in the memory of the server that holds its connection: several servers need sticky sessions (the client always reaches the
/// same server). A SignalR backplane is not needed: a session only talks to its own connection.
/// </remarks>
public sealed class RouteableSessions : IAsyncDisposable
{
    // Every move of a session (a connection that opens, drops or resumes it, the end of its grace) is made under _gate, so that a client that
    // comes back while its old connection is being closed, or before the server noticed it dropped, always finds its session.
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, RouteableSession> _sessions = new(StringComparer.Ordinal);   // connection → session
    private readonly Dictionary<string, string> _connectionOf = new(StringComparer.Ordinal);                  // token → its connection
    private readonly Dictionary<string, Detached> _detached = new(StringComparer.Ordinal);                     // token → session that waits
    private readonly TimeSpan _grace;
    private readonly int _maxDetached;
    private readonly int _maxDetachedPerUser;
    private readonly ILogger _logger;
    private long _order;

    /// <summary>Creates the registry.</summary>
    public RouteableSessions(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = services.GetService<RouteablesOptions>() ?? new RouteablesOptions();
        _grace = options.ReconnectGracePeriod;
        _maxDetached = options.MaxDetachedSessions;
        _maxDetachedPerUser = options.MaxDetachedSessionsPerUser;
        _logger = services.GetService<ILoggerFactory>()?.CreateLogger<RouteableSessions>() ?? NullLogger<RouteableSessions>.Instance;
    }

    private sealed record Detached(RouteableSession Session, Timer Expiry, long Order);

    // The Hello that resumes a session, by connection: the other calls of the connection wait for it.
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _greetings = new(StringComparer.Ordinal);

    internal IDisposable BeginGreeting(string connectionId)
    {
        var greeting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _greetings[connectionId] = greeting;
        return new Greeting(this, connectionId, greeting);
    }

    internal Task? GreetingOf(string connectionId) => _greetings.TryGetValue(connectionId, out var greeting) ? greeting.Task : null;

    private sealed class Greeting(RouteableSessions sessions, string connectionId, TaskCompletionSource greeting) : IDisposable
    {
        public void Dispose()
        {
            sessions._greetings.TryRemove(new KeyValuePair<string, TaskCompletionSource>(connectionId, greeting));
            greeting.TrySetResult();
        }
    }

    internal void Add(string connectionId, RouteableSession session)
    {
        RouteableSession? replaced;
        lock (_gate)
        {
            if (_sessions.TryGetValue(connectionId, out replaced))
                _connectionOf.Remove(replaced.Token);
            _sessions[connectionId] = session;
            _connectionOf[session.Token] = connectionId;
        }
        if (replaced is null)
            RouteablesTelemetry.Sessions.Add(1);
        else
            _ = replaced.DisposeAsync().AsTask();
    }

    internal RouteableSession? Get(string connectionId) => _sessions.TryGetValue(connectionId, out var session) ? session : null;

    /// <summary>
    /// The connection closed: its session waits for the client, when the client said <c>Hello</c> and may resume it, or is disposed. The sessions
    /// that wait are bounded (<see cref="RouteablesOptions.MaxDetachedSessions"/>, <see cref="RouteablesOptions.MaxDetachedSessionsPerUser"/>):
    /// beyond, the one that waits for the longest is disposed.
    /// </summary>
    internal async Task DisconnectAsync(string connectionId)
    {
        RouteableSession? session;
        var disposed = new List<RouteableSession>();
        lock (_gate)
        {
            if (!_sessions.TryRemove(connectionId, out session))
                return;
            RouteablesTelemetry.Sessions.Add(-1);
            if (!_connectionOf.TryGetValue(session.Token, out var current) || current != connectionId)
                return;   // another connection resumed it already
            _connectionOf.Remove(session.Token);

            if (_grace <= TimeSpan.Zero || session.IsDisposed || !session.Greeted || session.UserKey is null || _maxDetached == 0)
                disposed.Add(session);
            else
            {
                session.Detach();
                disposed.AddRange(MakeRoomFor(session.UserKey));
                var timer = new Timer(static state =>
                {
                    var (self, token) = ((RouteableSessions, string))state!;
                    _ = self.ExpireAsync(token);
                }, (this, session.Token), Timeout.Infinite, Timeout.Infinite);
                _detached[session.Token] = new Detached(session, timer, ++_order);
                RouteablesTelemetry.DetachedSessions.Add(1);
                timer.Change(_grace, Timeout.InfiniteTimeSpan);
            }
        }
        foreach (var gone in disposed)
            await DisposeQuietlyAsync(gone).ConfigureAwait(false);
    }

    // Under _gate: the sessions that wait for the longest leave room for one more, overall and for its user (an anonymous one has no user to count).
    private List<RouteableSession> MakeRoomFor(string userKey)
    {
        var evicted = new List<RouteableSession>();
        void Evict(Func<Detached, bool> which)
        {
            var oldest = _detached.Values.Where(which).MinBy(d => d.Order)!;
            _detached.Remove(oldest.Session.Token);
            oldest.Expiry.Dispose();
            RouteablesTelemetry.DetachedSessions.Add(-1);
            RouteablesTelemetry.DetachedEvicted.Add(1);
            evicted.Add(oldest.Session);
        }

        while (_detached.Count >= _maxDetached)
            Evict(_ => true);
        if (userKey.Length > 0)
            while (_detached.Values.Count(d => d.Session.UserKey == userKey) >= _maxDetachedPerUser)
                Evict(d => d.Session.UserKey == userKey);
        if (evicted.Count > 0)
            _logger.LogWarning("{Count} session(s) waiting for their client were disposed to keep the number of waiting sessions within its limits.", evicted.Count);
        return evicted;
    }

    /// <summary>
    /// Gives a session to a new connection, when the token is known and the user is the same: a session whose connection dropped, or one whose
    /// connection the server still holds (the client came back before the server noticed): that connection is then closed.
    /// </summary>
    internal async Task<RouteableSession?> ResumeAsync(string token, string connectionId, IRouteableClient client, ClaimsPrincipal? user, Action? abort)
    {
        RouteableSession session;
        RouteableSession? fresh;
        Timer? expiry = null;
        RouteableSession? stolenFrom = null;
        var key = RouteableSession.KeyOf(user);
        lock (_gate)
        {
            if (_detached.TryGetValue(token, out var detached))
            {
                if (!SameUser(detached.Session, key))
                    return null;
                _detached.Remove(token);
                expiry = detached.Expiry;
                session = detached.Session;
                RouteablesTelemetry.DetachedSessions.Add(-1);
            }
            else if (_connectionOf.TryGetValue(token, out var held) && held != connectionId && _sessions.TryGetValue(held, out var attached))
            {
                if (!SameUser(attached, key))
                    return null;
                _sessions.TryRemove(held, out _);   // the old connection no longer reaches it; its end will not touch it
                RouteablesTelemetry.Sessions.Add(-1);
                session = attached;
                stolenFrom = attached;
            }
            else
                return null;

            // The session made for the new connection is replaced by the one that is resumed.
            _sessions.TryRemove(connectionId, out fresh);
            if (fresh is not null)
            {
                _connectionOf.Remove(fresh.Token);
                RouteablesTelemetry.Sessions.Add(-1);
            }
            _sessions[connectionId] = session;
            _connectionOf[token] = connectionId;
            RouteablesTelemetry.Sessions.Add(1);
        }

        if (expiry is not null)
            await expiry.DisposeAsync().ConfigureAwait(false);
        if (fresh is not null)
            await DisposeQuietlyAsync(fresh).ConfigureAwait(false);
        if (stolenFrom is not null)
        {
            _logger.LogInformation("A session was resumed while the server still held its old connection: that connection is closed.");
            stolenFrom.Abort();
            stolenFrom.Detach();
        }
        await session.AttachAsync(client, connectionId, user, abort).ConfigureAwait(false);
        return session;

        bool SameUser(RouteableSession candidate, string? userKey)
        {
            if (candidate.UserKey is not null && candidate.UserKey == userKey)
                return true;
            _logger.LogWarning("A connection tried to resume a session it may not resume (another user, or a user without a stable identifier).");
            return false;
        }
    }

    private async Task ExpireAsync(string token)
    {
        Detached? detached;
        lock (_gate)
        {
            if (!_detached.Remove(token, out detached))
                return;
            RouteablesTelemetry.DetachedSessions.Add(-1);
        }
        await detached.Expiry.DisposeAsync().ConfigureAwait(false);
        await DisposeQuietlyAsync(detached.Session).ConfigureAwait(false);
    }

    private async Task DisposeQuietlyAsync(RouteableSession session)
    {
        try { await session.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { _logger.LogError(exception, "A session could not be disposed."); }
    }

    /// <summary>
    /// Evaluates again what the users of the sessions may see (<c>[Authorize]</c> on the view models, <c>[RouteableAuthorize]</c> on the properties,
    /// at any depth), and sends every target again: call it when permissions changed (a role withdrawn, a resource shared). The calls (commands,
    /// properties set, routes) are authorized at every call already.
    /// </summary>
    /// <param name="users">The users whose sessions are evaluated again; every session when <see langword="null"/>.</param>
    public Task ReauthorizeAsync(Func<ClaimsPrincipal?, bool>? users = null)
    {
        List<RouteableSession> sessions;
        lock (_gate)
            sessions = [.. _sessions.Values.Distinct()];
        return Task.WhenAll(sessions.Where(s => users is null || users(s.User)).Select(s => s.ReauthorizeAsync()));
    }

    /// <summary>How many connections have a session.</summary>
    public int Count => _sessions.Count;

    /// <summary>How many sessions wait for their client to come back.</summary>
    public int DetachedCount
    {
        get
        {
            lock (_gate)
                return _detached.Count;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        List<string> tokens;
        lock (_gate)
            tokens = [.. _detached.Keys];
        foreach (var token in tokens)
            await ExpireAsync(token).ConfigureAwait(false);
        foreach (var connection in _sessions.Keys.ToList())
            if (_sessions.TryRemove(connection, out var session))
                await DisposeQuietlyAsync(session).ConfigureAwait(false);
    }
}
