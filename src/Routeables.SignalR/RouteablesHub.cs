using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;

namespace Routeables.SignalR;

/// <summary>
/// The hub a browser client talks to: one <see cref="RouteableSession"/> per connection. The client asks (<c>GoTo</c>, <c>Invoke</c>,
/// <c>Set</c>); the server tells through <see cref="IRouteableClient"/>.
/// </summary>
public class RouteablesHub(IServiceProvider services, RouteableSessions sessions) : Hub<IRouteableClient>
{
    /// <inheritdoc />
    public override Task OnConnectedAsync()
    {
        sessions.Add(Context.ConnectionId, new RouteableSession(services, Clients.Caller));
        return base.OnConnectedAsync();
    }

    /// <inheritdoc />
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await sessions.RemoveAsync(Context.ConnectionId).ConfigureAwait(false);
        await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
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
    public RouteableAck Set(string property, JsonElement value)
        => Session.Set(property, value);

    /// <summary>Starts to follow a region or a registered view model (<c>$name</c>): the client is told what it shows and what changes.</summary>
    public RouteableAck Watch(string target) => Session.Watch(target);

    /// <summary>Stops following a target.</summary>
    public RouteableAck Unwatch(string target) => Session.Unwatch(target);

    /// <summary>Goes to a route in the router of a target (the main page is the empty target, a region its name).</summary>
    public Task<RouteableAck> GoToIn(string target, string route)
        => Session.GoToInAsync(target, route, Context.ConnectionAborted);

    /// <summary>Calls a command of the view model a target shows: a method that says so, or a command of its state by path.</summary>
    public Task<RouteableAck> InvokeOn(string target, string command, JsonElement[]? arguments)
        => Session.InvokeOnAsync(target, command, arguments);

    /// <summary>Sets a property of the view model a target shows.</summary>
    public RouteableAck SetOn(string target, string property, JsonElement value)
        => Session.SetOn(target, property, value);

    /// <summary>Answers a dialog: the index of the button that was chosen.</summary>
    public RouteableAck Answer(string id, int button) => Session.Answer(id, button);

    /// <summary>A notification was closed (or clicked when <paramref name="click"/> is true).</summary>
    public RouteableAck Dismiss(int id, bool click) => Session.Dismiss(id, click);

    private RouteableSession Session
        => sessions.Get(Context.ConnectionId) ?? throw new HubException("The session of this connection is closed.");
}

/// <summary>The sessions of the open connections.</summary>
public sealed class RouteableSessions
{
    private readonly ConcurrentDictionary<string, RouteableSession> _sessions = new(StringComparer.Ordinal);

    internal void Add(string connectionId, RouteableSession session) => _sessions[connectionId] = session;

    internal RouteableSession? Get(string connectionId) => _sessions.TryGetValue(connectionId, out var session) ? session : null;

    internal async Task RemoveAsync(string connectionId)
    {
        if (_sessions.TryRemove(connectionId, out var session))
            await session.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>How many connections have a session.</summary>
    public int Count => _sessions.Count;
}
