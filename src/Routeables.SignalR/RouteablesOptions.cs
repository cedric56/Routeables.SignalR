namespace Routeables.SignalR;

/// <summary>Which properties of a view model cross the network.</summary>
public enum RouteableExposure
{
    /// <summary>
    /// Only what says so: a property marked <see cref="RouteableStateAttribute"/>, every public property of a class marked with it, or of a type
    /// accepted by <see cref="RouteablesOptions.ExposeTypes"/>. The default: a property added to a view model stays on the server until it is exposed.
    /// </summary>
    Explicit,

    /// <summary>
    /// Every public readable property, except those marked <see cref="System.Text.Json.Serialization.JsonIgnoreAttribute"/>; every public setter can be
    /// called by the client. The behaviour of the first versions: convenient for a prototype, unsafe for view models that hold more than the screen.
    /// </summary>
    Public,
}

/// <summary>The options of Routeables.SignalR: <c>AddRouteablesSignalR(..., options =&gt; ...)</c>.</summary>
public sealed class RouteablesOptions
{
    private readonly List<(Func<Type, bool> Predicate, bool Settable)> _exposedTypes = [];

    /// <summary>Which properties cross the network. <see cref="RouteableExposure.Explicit"/> by default.</summary>
    public RouteableExposure Exposure { get; set; } = RouteableExposure.Explicit;

    /// <summary>
    /// Exposes every public property of the types that <paramref name="predicate"/> accepts, as if the class carried
    /// <c>[RouteableState(Settable = <paramref name="settable"/>)]</c>: for view models of a library that cannot be annotated.
    /// </summary>
    /// <param name="predicate">The types to expose.</param>
    /// <param name="settable">Whether the client may also set their properties that have a public setter.</param>
    public RouteablesOptions ExposeTypes(Func<Type, bool> predicate, bool settable = false)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        _exposedTypes.Add((predicate, settable));
        return this;
    }

    /// <summary>How many targets (regions, registered view models) a connection may watch, besides the page and the modal. 24 by default.</summary>
    public int MaxTargets { get; set; } = 24;

    /// <summary>
    /// How long a call (a command, a route change) is awaited before the hub answers that it goes on. A command that asks the visitor
    /// something is answered as soon as it asks. 800 ms by default.
    /// </summary>
    public TimeSpan CommandGrace { get; set; } = TimeSpan.FromMilliseconds(800);

    /// <summary>How deep the state of a view model is read (objects inside objects). 4 by default.</summary>
    public int MaxDepth { get; set; } = 4;

    /// <summary>
    /// How many items a collection may have to be sent. A larger one is sent as <see langword="null"/> and the client is told why
    /// (<see cref="IRouteableClient.Failed"/>, code <c>limit</c>): page it. 10 000 by default.
    /// </summary>
    public int MaxCollectionItems { get; set; } = 10_000;

    /// <summary>How many values (scalars, objects, items) one property may hold once projected; beyond, it is sent as <see langword="null"/>. 100 000 by default.</summary>
    public int MaxStateNodes { get; set; } = 100_000;

    /// <summary>The longest JSON argument (in characters) a client may send to a command or a property. 64 KiB by default.</summary>
    public int MaxArgumentLength { get; set; } = 64 * 1024;

    /// <summary>How many values (array items and object members, at any depth) an argument may hold. 10 000 by default.</summary>
    public int MaxArgumentItems { get; set; } = 10_000;

    /// <summary>How deep an argument may nest. 32 by default.</summary>
    public int MaxArgumentDepth { get; set; } = 32;

    /// <summary>
    /// How many messages may wait for a slow client, after the changes of a property were merged. Beyond, the connection is closed: the
    /// client reconnects, resumes its session and receives the state again. 1 000 by default.
    /// </summary>
    public int MaxPendingMessages { get; set; } = 1_000;

    /// <summary>
    /// How long the session of a connection that dropped is kept for the client to resume it (its view models, its dialogs).
    /// <see cref="TimeSpan.Zero"/> disposes it at once. 30 seconds by default.
    /// </summary>
    public TimeSpan ReconnectGracePeriod { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How many sessions may wait for their client at once, on this server. Beyond, the one that waits for the longest is disposed: a client
    /// that opens and drops connections in a loop cannot fill the memory. Only a session whose client said <c>Hello</c> waits. 10 000 by default.
    /// </summary>
    public int MaxDetachedSessions { get; set; } = 10_000;

    /// <summary>
    /// How many sessions of one authenticated user may wait for their client at once (tabs, devices). Beyond, the one that waits for the longest
    /// is disposed. Anonymous sessions are counted by <see cref="MaxDetachedSessions"/> only. 16 by default.
    /// </summary>
    public int MaxDetachedSessionsPerUser { get; set; } = 16;

    /// <summary>
    /// Whether the message of an exception thrown by a view model is sent to the client. When <see langword="null"/> (the default), it is sent
    /// in the Development environment only; elsewhere the client gets a generic message and the exception is logged. The message of a
    /// <see cref="RouteableException"/> is always sent.
    /// </summary>
    public bool? DetailedErrors { get; set; }

    /// <summary>
    /// Whether a connection must be authenticated. The hub endpoint should be protected too (<c>MapRouteables().RequireAuthorization()</c>);
    /// this closes the connections that reach it anyway. <see langword="false"/> by default.
    /// </summary>
    public bool RequireAuthenticatedUser { get; set; }

    /// <summary>
    /// What is wrong with these options: a limit that is not positive, a duration that is negative, limits that contradict each other.
    /// <c>AddRouteablesSignalR</c> refuses them (<see cref="InvalidOperationException"/>), so that the application does not start with them.
    /// </summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        void AtLeast(string name, long value, long minimum)
        {
            if (value < minimum)
                problems.Add($"{name} is {value}: it must be {minimum} or more.");
        }

        if (!Enum.IsDefined(Exposure))
            problems.Add($"Exposure is {(int)Exposure}: it is not a {nameof(RouteableExposure)}.");
        AtLeast(nameof(MaxTargets), MaxTargets, 0);
        AtLeast(nameof(MaxDepth), MaxDepth, 1);
        AtLeast(nameof(MaxCollectionItems), MaxCollectionItems, 1);
        AtLeast(nameof(MaxStateNodes), MaxStateNodes, 1);
        AtLeast(nameof(MaxArgumentLength), MaxArgumentLength, 1);
        AtLeast(nameof(MaxArgumentItems), MaxArgumentItems, 1);
        AtLeast(nameof(MaxArgumentDepth), MaxArgumentDepth, 1);
        AtLeast(nameof(MaxPendingMessages), MaxPendingMessages, 1);
        AtLeast(nameof(MaxDetachedSessions), MaxDetachedSessions, 0);
        AtLeast(nameof(MaxDetachedSessionsPerUser), MaxDetachedSessionsPerUser, 1);
        if (MaxDepth > 64)
            problems.Add($"MaxDepth is {MaxDepth}: it must be 64 or less (a state is read recursively).");
        if (MaxStateNodes >= 1 && MaxCollectionItems > MaxStateNodes)
            problems.Add($"MaxCollectionItems ({MaxCollectionItems}) is larger than MaxStateNodes ({MaxStateNodes}): a collection that fits would still not be sent.");
        if (CommandGrace < TimeSpan.Zero || CommandGrace == Timeout.InfiniteTimeSpan)
            problems.Add($"CommandGrace is {CommandGrace}: it must be zero or more, and finite.");
        if (ReconnectGracePeriod < TimeSpan.Zero || ReconnectGracePeriod == Timeout.InfiniteTimeSpan)
            problems.Add($"ReconnectGracePeriod is {ReconnectGracePeriod}: it must be zero or more, and finite.");
        return problems;
    }

    internal void Validate()
    {
        var problems = Problems();
        if (problems.Count > 0)
            throw new InvalidOperationException("The options of Routeables.SignalR are not valid: " + string.Join(" ", problems));
    }

    internal (bool Exposed, bool Settable) ExposedType(Type type)
    {
        var exposed = false;
        var settable = false;
        foreach (var (predicate, canSet) in _exposedTypes)
        {
            if (!predicate(type))
                continue;
            exposed = true;
            settable |= canSet;
        }
        return (exposed, settable);
    }
}
