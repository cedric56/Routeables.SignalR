using System.Globalization;
using System.Text.Json;
using Microsoft.Maui.ApplicationModel;

namespace Routeables.SignalR.Essentials;

/// <summary>
/// The base of the Essentials services of a session: one service of the client (<see cref="Service"/>), its announced state, its calls and
/// its events. The MAUI services of this package derive from it; an application can too, for a device API of its own.
/// </summary>
/// <remarks>
/// A synchronous member reads the state (<see cref="TryGetState"/>), refreshed by <see cref="OnReady"/> at each announcement and by events;
/// an asynchronous one calls the client (<see cref="CallAsync"/>); a synchronous command (<c>Vibrate</c>, <c>Announce</c>) is
/// <see cref="Post"/>ed: sent, not waited for. Failures on the client become the MAUI exceptions (<see cref="FeatureNotSupportedException"/>,
/// <see cref="PermissionException"/>…).
/// </remarks>
public abstract class RemoteService : IDisposable
{
    private readonly List<IDisposable> _subscriptions = [];
    private bool _disposed;

    /// <summary>Creates the service <paramref name="service"/> of a session.</summary>
    protected RemoteService(RouteableEssentials essentials, string service)
    {
        Essentials = essentials ?? throw new ArgumentNullException(nameof(essentials));
        Service = service;
        Essentials.Ready += Refresh;
    }

    /// <summary>The device APIs of the client of the session.</summary>
    protected RouteableEssentials Essentials { get; }

    /// <summary>The name of the service on the client (<c>battery</c>).</summary>
    protected string Service { get; }

    /// <summary>Whether the client announced this service (false before it announced anything).</summary>
    protected bool IsAvailable => Essentials.IsSupported(Service);

    /// <summary>
    /// Call at the end of the constructor of the derived class: reads the state the client already announced, if it did. The service is
    /// often created by the first view model that asks for it, after the announcement.
    /// </summary>
    protected void Initialize()
    {
        if (Essentials.HasAnnounced)
            Refresh();
    }

    private void Refresh()
    {
        if (!_disposed)
            OnReady();
    }

    /// <summary>The client announced its services (again, after a reconnection): read the state, restart what was running.</summary>
    protected virtual void OnReady()
    {
    }

    /// <summary>The state of the service the client announced last.</summary>
    protected bool TryGetState(out JsonElement state)
    {
        if (IsAvailable && Essentials.TryGetState(Service, out state) && state.ValueKind == JsonValueKind.Object)
            return true;
        state = default;
        return false;
    }

    /// <summary>Throws <see cref="FeatureNotSupportedException"/> when the client announced that it does not have this service.</summary>
    protected void EnsureSupported()
    {
        if (Essentials.HasAnnounced && !IsAvailable)
            throw new FeatureNotSupportedException($"The client of this session does not support {Service}.");
    }

    /// <summary>Calls <paramref name="method"/> on the client; its failures are thrown as the MAUI exceptions.</summary>
    protected async Task<JsonElement> CallAsync(
        string method,
        IReadOnlyList<object?>? args = null,
        EssentialsCallOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        EnsureSupported();
        try
        {
            return await Essentials.InvokeAsync(Service, method, args, options, cancellationToken).ConfigureAwait(true);
        }
        catch (RouteableEssentialsException exception)
        {
            var translated = exception.ToMauiException();
            if (ReferenceEquals(translated, exception))
                throw;
            throw translated;
        }
    }

    private static readonly EssentialsCallOptions Posted = new() { Ask = false };

    /// <summary>Sends <paramref name="method"/> to the client without waiting for it (a synchronous MAUI member). A failure is dropped.</summary>
    protected void Post(string method, IReadOnlyList<object?>? args = null)
    {
        EnsureSupported();
        var call = Essentials.InvokeAsync(Service, method, args, Posted);
        _ = call.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    /// <summary>Receives <paramref name="eventName"/> of the service, on the dispatcher of the session, as long as this service lives.</summary>
    protected IDisposable Subscribe(string eventName, Action<JsonElement?> handler)
    {
        var subscription = Essentials.Subscribe(Service, eventName, handler);
        lock (_subscriptions)
            _subscriptions.Add(subscription);
        return new Unsubscriber(this, subscription);
    }

    private sealed class Unsubscriber(RemoteService owner, IDisposable subscription) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._subscriptions)
                owner._subscriptions.Remove(subscription);
            subscription.Dispose();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Stops the events and the state updates.</summary>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;
        _disposed = true;
        Essentials.Ready -= Refresh;
        IDisposable[] subscriptions;
        lock (_subscriptions)
        {
            subscriptions = [.. _subscriptions];
            _subscriptions.Clear();
        }
        foreach (var subscription in subscriptions)
            subscription.Dispose();
    }
}

/// <summary>Reads what a client sends, without trusting it: a missing or wrong member gives the default.</summary>
internal static class EssentialsJson
{
    public static bool IsNull(JsonElement value) => value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null;

    public static string? String(JsonElement value, string name, int maxLength = 4096)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? Limit(p.GetString(), maxLength)
            : null;

    public static string? Limit(string? text, int maxLength) => text is null || text.Length <= maxLength ? text : text[..maxLength];

    public static double? Double(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number
           && p.TryGetDouble(out var d) && double.IsFinite(d)
            ? d
            : null;

    public static bool? Bool(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var p) && p.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? p.GetBoolean()
            : null;

    public static T Enum<T>(JsonElement value, string name, T fallback) where T : struct, System.Enum
        => String(value, name, 64) is { } text && System.Enum.TryParse<T>(text, ignoreCase: true, out var parsed) && System.Enum.IsDefined(parsed)
            ? parsed
            : fallback;

    public static IEnumerable<JsonElement> Array(JsonElement value, int max = 1000)
        => value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Take(max) : [];

    public static IEnumerable<JsonElement> Array(JsonElement value, string name, int max = 1000)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var p) ? Array(p, max) : [];

    public static List<string> Strings(JsonElement value, string name, int max = 100)
        => [.. Array(value, name, max).Where(e => e.ValueKind == JsonValueKind.String).Select(e => Limit(e.GetString(), 1024)!)];

    public static Version Version(string? text)
        => System.Version.TryParse(text, out var version) ? version
            : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var major) ? new Version(major, 0)
            : new Version(0, 0);

    /// <summary>A time: milliseconds since 1970 (what JavaScript gives) or an ISO 8601 text.</summary>
    public static DateTimeOffset? Time(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var p))
            return null;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var ms) && double.IsFinite(ms) && Math.Abs(ms) < 8.64e15)
            return DateTimeOffset.FromUnixTimeMilliseconds((long)ms);
        if (p.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(p.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time))
            return time;
        return null;
    }
}
