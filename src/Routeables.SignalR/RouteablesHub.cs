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
        if (!string.IsNullOrEmpty(hello.Resume) && hello.Resume != current.Token)
        {
            var context = Context;
            var resumed = await sessions.ResumeAsync(hello.Resume, Context.ConnectionId, Clients.Caller, Context.User, context.Abort).ConfigureAwait(false);
            if (resumed is not null)
            {
                await resumed.FlushAsync().ConfigureAwait(false);
                return new RouteableWelcome(true, RouteableProtocol.Version, resumed.Token, true);
            }
        }
        return new RouteableWelcome(true, RouteableProtocol.Version, current.Token, false);
    }

    /// <summary>Goes to a route, as the address bar of a browser would.</summary>
    public Task<RouteableAck> GoTo(string route)
        => Session.GoToAsync(route, Context.ConnectionAborted);

    /// <summary>
    /// Calls a command of the current view model. SignalR does not support optional parameters: the client always sends the
    /// arguments, an empty array when the command has none.
    /// </summary>
    public Task<RouteableAck> Invoke(string command, JsonElement[]? arguments)
        => Session.InvokeAsync(command, arguments);

    /// <summary>Sets a property of the current view model.</summary>
    public Task<RouteableAck> Set(string property, JsonElement value)
        => Session.SetAsync(property, value);

    /// <summary>Starts to follow a region or a registered view model (<c>$name</c>): the client is told what it shows and what changes.</summary>
    public Task<RouteableAck> Watch(string target) => Session.WatchAsync(target);

    /// <summary>Stops following a target.</summary>
    public Task<RouteableAck> Unwatch(string target) => Session.UnwatchAsync(target);

    /// <summary>Sends the full state of a target again (a change was lost).</summary>
    public Task<RouteableAck> Resync(string target) => Session.ResyncAsync(target);

    /// <summary>Goes to a route in the router of a target (the main page is the empty target, a region its name).</summary>
    public Task<RouteableAck> GoToIn(string target, string route)
        => Session.GoToInAsync(target, route, Context.ConnectionAborted);

    /// <summary>Calls a command of the view model a target shows: a method that says so, or a command of its state by path.</summary>
    public Task<RouteableAck> InvokeOn(string target, string command, JsonElement[]? arguments)
        => Session.InvokeOnAsync(target, command, arguments);

    /// <summary>Sets a property of the view model a target shows.</summary>
    public Task<RouteableAck> SetOn(string target, string property, JsonElement value)
        => Session.SetOnAsync(target, property, value);

    /// <summary>Answers a dialog: the index of the button that was chosen.</summary>
    public Task<RouteableAck> Answer(string id, int button) => Session.AnswerAsync(id, button);

    /// <summary>A notification was closed (or clicked when <paramref name="click"/> is true).</summary>
    public Task<RouteableAck> Dismiss(int id, bool click) => Session.DismissAsync(id, click);

    private RouteableSession Session
        => sessions.Get(Context.ConnectionId) ?? throw new HubException("The session of this connection is closed.");
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
    private readonly ConcurrentDictionary<string, RouteableSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Detached> _detached = new(StringComparer.Ordinal);
    private readonly TimeSpan _grace;
    private readonly ILogger _logger;

    /// <summary>Creates the registry.</summary>
    public RouteableSessions(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _grace = services.GetService<RouteablesOptions>()?.ReconnectGracePeriod ?? TimeSpan.FromSeconds(30);
        _logger = services.GetService<ILoggerFactory>()?.CreateLogger<RouteableSessions>() ?? NullLogger<RouteableSessions>.Instance;
    }

    private sealed record Detached(RouteableSession Session, Timer Expiry);

    internal void Add(string connectionId, RouteableSession session)
    {
        _sessions[connectionId] = session;
        RouteablesTelemetry.Sessions.Add(1);
    }

    internal RouteableSession? Get(string connectionId) => _sessions.TryGetValue(connectionId, out var session) ? session : null;

    /// <summary>The connection closed: its session waits for the client, or is disposed when no grace is given.</summary>
    internal async Task DisconnectAsync(string connectionId)
    {
        if (!_sessions.TryRemove(connectionId, out var session))
            return;
        RouteablesTelemetry.Sessions.Add(-1);

        if (_grace <= TimeSpan.Zero || session.IsDisposed)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            return;
        }

        session.Detach();
        var timer = new Timer(static state =>
        {
            var (self, token) = ((RouteableSessions, string))state!;
            _ = self.ExpireAsync(token);
        }, (this, session.Token), Timeout.Infinite, Timeout.Infinite);
        _detached[session.Token] = new Detached(session, timer);
        RouteablesTelemetry.DetachedSessions.Add(1);
        timer.Change(_grace, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Gives a session that waits to a new connection, when the token is known and the user is the same.</summary>
    internal async Task<RouteableSession?> ResumeAsync(string token, string connectionId, IRouteableClient client, ClaimsPrincipal? user, Action? abort)
    {
        if (!_detached.TryGetValue(token, out var detached))
            return null;
        if (detached.Session.UserKey != RouteableSession.KeyOf(user))
        {
            _logger.LogWarning("A connection tried to resume the session of another user.");
            return null;
        }
        if (!_detached.TryRemove(new KeyValuePair<string, Detached>(token, detached)))
            return null;   // resumed or expired meanwhile

        await detached.Expiry.DisposeAsync().ConfigureAwait(false);
        RouteablesTelemetry.DetachedSessions.Add(-1);

        // The session made for the new connection is replaced by the one that is resumed.
        if (_sessions.TryGetValue(connectionId, out var fresh))
            await fresh.DisposeAsync().ConfigureAwait(false);
        else
            RouteablesTelemetry.Sessions.Add(1);
        _sessions[connectionId] = detached.Session;

        await detached.Session.AttachAsync(client, connectionId, user, abort).ConfigureAwait(false);
        return detached.Session;
    }

    private async Task ExpireAsync(string token)
    {
        if (!_detached.TryRemove(token, out var detached))
            return;
        RouteablesTelemetry.DetachedSessions.Add(-1);
        await detached.Expiry.DisposeAsync().ConfigureAwait(false);
        try { await detached.Session.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) { _logger.LogError(exception, "A session could not be disposed."); }
    }

    /// <summary>How many connections have a session.</summary>
    public int Count => _sessions.Count;

    /// <summary>How many sessions wait for their client to come back.</summary>
    public int DetachedCount => _detached.Count;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var token in _detached.Keys.ToList())
            await ExpireAsync(token).ConfigureAwait(false);
        foreach (var connection in _sessions.Keys.ToList())
            if (_sessions.TryRemove(connection, out var session))
                await session.DisposeAsync().ConfigureAwait(false);
    }
}
