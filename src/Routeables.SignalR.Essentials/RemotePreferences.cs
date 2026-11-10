using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;

namespace Routeables.SignalR.Essentials;

/// <summary>
/// <see cref="IPreferences"/> of the client of a session (the <c>localStorage</c> of the browser, the preferences of the app). The client sends
/// every value with <see cref="EssentialsReady"/>: <c>Get</c> and <c>ContainsKey</c> read them on the server, without a round trip. <c>Set</c>,
/// <c>Remove</c> and <c>Clear</c> change them at once on the server and are written on the client in the background, in order;
/// <see cref="FlushAsync"/> waits for them and reports their failures.
/// </summary>
/// <remarks>
/// <para>Service <c>preferences</c>. State: <c>{ "&lt;container&gt;": { "&lt;key&gt;": "&lt;value&gt;" } }</c>, the container being the shared name
/// (<c>""</c> without one). Methods: <c>set(container, key, value)</c>, <c>remove(container, key)</c>, <c>clear(container)</c>.</para>
/// <para>The values are strings written with the invariant culture; as in MAUI, only <c>string</c>, <c>bool</c>, <c>int</c>, <c>long</c>,
/// <c>float</c>, <c>double</c>, <c>DateTime</c> and <c>DateTimeOffset</c> are supported.</para>
/// <para>A write made before the client announced its values, or that it has not acknowledged when the connection drops, is applied again over the
/// values it announces (and sent again): it is never lost to a reconnection.</para>
/// </remarks>
public sealed class RemotePreferences : IPreferences, IDisposable
{
    internal const string Service = "preferences";

    private static readonly EssentialsCallOptions Write = new() { Replay = true, Ask = false };

    private readonly RouteableEssentials _essentials;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<(string Container, string Key), string> _values = new();
    private readonly List<Operation> _unacknowledged = [];
    private readonly HashSet<Task> _pending = [];
    private readonly ConcurrentQueue<Exception> _errors = new();

    private sealed record Operation(string Kind, string Container, string? Key, string? Value);

    /// <summary>Creates the preferences of a session.</summary>
    public RemotePreferences(RouteableEssentials essentials)
    {
        _essentials = essentials ?? throw new ArgumentNullException(nameof(essentials));
        _essentials.Ready += Load;
        if (_essentials.HasAnnounced)
            Load();
    }

    private static string ContainerOf(string? sharedName) => sharedName ?? string.Empty;

    // Each announcement replaces the values (another tab may have changed them), then the writes not acknowledged yet are applied again.
    private void Load()
    {
        lock (_gate)
        {
            _values.Clear();
            if (_essentials.TryGetState(Service, out var state) && state.ValueKind == JsonValueKind.Object)
            {
                foreach (var container in state.EnumerateObject())
                {
                    if (container.Value.ValueKind != JsonValueKind.Object)
                        continue;
                    foreach (var entry in container.Value.EnumerateObject())
                        if (entry.Value.ValueKind == JsonValueKind.String)
                            _values[(container.Name, entry.Name)] = entry.Value.GetString()!;
                }
            }
            foreach (var operation in _unacknowledged)
                Apply(operation);
        }
    }

    private void Apply(Operation operation)
    {
        switch (operation.Kind)
        {
            case "set":
                _values[(operation.Container, operation.Key!)] = operation.Value!;
                break;
            case "remove":
                _values.TryRemove((operation.Container, operation.Key!), out _);
                break;
            case "clear":
                foreach (var key in _values.Keys.Where(k => k.Container == operation.Container).ToList())
                    _values.TryRemove(key, out _);
                break;
        }
    }

    private void EnsureAvailable()
    {
        if (_essentials.HasAnnounced && !_essentials.IsSupported(Service))
            throw new FeatureNotSupportedException("The client of this session does not support preferences.");
    }

    /// <inheritdoc />
    public bool ContainsKey(string key, string? sharedName = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        EnsureAvailable();
        return _values.ContainsKey((ContainerOf(sharedName), key));
    }

    /// <inheritdoc />
    public void Remove(string key, string? sharedName = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        Change(new Operation("remove", ContainerOf(sharedName), key, null), [ContainerOf(sharedName), key]);
    }

    /// <inheritdoc />
    public void Clear(string? sharedName = null)
        => Change(new Operation("clear", ContainerOf(sharedName), null, null), [ContainerOf(sharedName)]);

    /// <inheritdoc />
    public void Set<T>(string key, T value, string? sharedName = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        EnsureSupported<T>();
        if (value is null)
        {
            Remove(key, sharedName);
            return;
        }
        var text = Serialize(value);
        Change(new Operation("set", ContainerOf(sharedName), key, text), [ContainerOf(sharedName), key, text]);
    }

    /// <inheritdoc />
    public T Get<T>(string key, T defaultValue, string? sharedName = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        EnsureSupported<T>();
        EnsureAvailable();
        return _values.TryGetValue((ContainerOf(sharedName), key), out var text) && TryParse<T>(text, out var value) ? value : defaultValue;
    }

    private void Change(Operation operation, object?[] args)
    {
        EnsureAvailable();
        Task write;
        lock (_gate)
        {
            Apply(operation);
            _unacknowledged.Add(operation);
            // Sent in the order of the calls: the channel keeps it, and the client applies them one after the other.
            write = _essentials.InvokeAsync(Service, operation.Kind, args, Write);
            _pending.Add(write);
        }
        _ = write.ContinueWith(task =>
        {
            lock (_gate)
            {
                _pending.Remove(task);
                _unacknowledged.Remove(operation);
            }
            if (task.Exception is { } exception)
                _errors.Enqueue(exception.InnerException ?? exception);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    /// Waits for the writes made so far to be acknowledged by the client, and throws (<see cref="AggregateException"/> of
    /// <see cref="RouteableEssentialsException"/>) if any failed since the last call.
    /// </summary>
    public async Task FlushAsync()
    {
        Task[] pending;
        lock (_gate)
            pending = [.. _pending];
        try
        {
            await Task.WhenAll(pending).ConfigureAwait(true);
        }
        catch
        {
            // Collected below.
        }

        // The continuations that record the failures run synchronously with the writes; they ran before WhenAll completed.
        var errors = new List<Exception>();
        while (_errors.TryDequeue(out var error))
            errors.Add(error);
        if (errors.Count > 0)
            throw new AggregateException("One or more preferences could not be written on the client.", errors);
    }

    // ---------------------------------------------------------------- values

    private static readonly Type[] SupportedTypes =
    [
        typeof(string), typeof(bool), typeof(int), typeof(long), typeof(float), typeof(double), typeof(DateTime), typeof(DateTimeOffset),
    ];

    internal static void EnsureSupported<T>()
    {
        var type = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (Array.IndexOf(SupportedTypes, type) < 0)
            throw new NotSupportedException($"Preferences does not support values of type {typeof(T).Name}.");
    }

    internal static string Serialize<T>(T value) => value switch
    {
        string s => s,
        bool b => b ? "true" : "false",
        DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => throw new NotSupportedException($"Preferences does not support values of type {typeof(T).Name}."),
    };

    internal static bool TryParse<T>(string text, out T value)
    {
        var type = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        object? parsed = null;
        var ok = type switch
        {
            _ when type == typeof(string) => Keep(text),
            _ when type == typeof(bool) => bool.TryParse(text, out var b) && Keep(b),
            _ when type == typeof(int) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && Keep(i),
            _ when type == typeof(long) => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) && Keep(l),
            _ when type == typeof(float) => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && Keep(f),
            _ when type == typeof(double) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && Keep(d),
            _ when type == typeof(DateTime) => DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt) && Keep(dt),
            _ when type == typeof(DateTimeOffset) => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto) && Keep(dto),
            _ => false,
        };
        value = ok ? (T)parsed! : default!;
        return ok;

        bool Keep(object result)
        {
            parsed = result;
            return true;
        }
    }

    /// <inheritdoc />
    public void Dispose() => _essentials.Ready -= Load;
}
