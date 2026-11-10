using System.Text.Json;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Devices.Sensors;

namespace Routeables.SignalR.Essentials;

/// <summary>
/// <see cref="IGeolocation"/> of the client. Service <c>geolocation</c>; state <c>{ isEnabled }</c>; methods <c>getLastKnown()</c>,
/// <c>getLocation({ desiredAccuracy, timeout (ms), requestFullAccuracy })</c>, <c>startListening({ desiredAccuracy, minimumTime (ms) })</c>,
/// <c>stopListening()</c>; events <c>locationChanged</c> (a location) and <c>listeningFailed</c> (<c>{ error }</c>). A location is
/// <c>{ latitude, longitude, altitude, accuracy, verticalAccuracy, speed, course, timestamp, reducedAccuracy, isFromMockProvider }</c>.
/// </summary>
/// <remarks>
/// A listener survives a reconnection: when the client announces itself again, it is asked to listen again with the same request.
/// </remarks>
public sealed class RemoteGeolocation : RemoteService, IGeolocation
{
    internal const string ServiceName = "geolocation";
    private readonly object _gate = new();
    private bool _enabled;
    private GeolocationListeningRequest? _listening;
    private IDisposable? _changed;
    private IDisposable? _failed;
    private Location? _last;

    /// <summary>Creates the geolocation of a session.</summary>
    public RemoteGeolocation(RouteableEssentials essentials) : base(essentials, ServiceName) => Initialize();

    /// <inheritdoc />
    protected override void OnReady()
    {
        _enabled = TryGetState(out var s) && (EssentialsJson.Bool(s, "isEnabled") ?? false);
        GeolocationListeningRequest? listening;
        lock (_gate)
            listening = _listening;
        if (listening is not null && IsAvailable)
            Post("startListening", [Describe(listening)]);
    }

    /// <inheritdoc />
    public bool IsEnabled => _enabled;

    /// <inheritdoc />
    public bool IsListeningForeground
    {
        get { lock (_gate) return _listening is not null; }
    }

    /// <inheritdoc />
    public event EventHandler<GeolocationLocationChangedEventArgs>? LocationChanged;

    /// <inheritdoc />
    public event EventHandler<GeolocationListeningFailedEventArgs>? ListeningFailed;

    /// <inheritdoc />
    public async Task<Location?> GetLastKnownLocationAsync()
    {
        var result = await CallAsync("getLastKnown").ConfigureAwait(true);
        return ParseLocation(result) ?? _last;
    }

    /// <inheritdoc />
    public async Task<Location?> GetLocationAsync(GeolocationRequest request, CancellationToken cancelToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            // The browser may ask the user first: the call has no timeout of its own, the request's is applied by the client.
            var result = await CallAsync("getLocation", [new
            {
                desiredAccuracy = request.DesiredAccuracy.ToString(),
                timeout = request.Timeout > TimeSpan.Zero ? request.Timeout.TotalMilliseconds : (double?)null,
                requestFullAccuracy = request.RequestFullAccuracy,
            }], EssentialsCallOptions.Interactive, cancelToken).ConfigureAwait(true);
            var location = ParseLocation(result);
            if (location is not null)
                _last = location;
            return location;
        }
        catch (TimeoutException)
        {
            return null;   // as MAUI: no position within the timeout
        }
        catch (PermissionException)
        {
            _enabled = false;
            throw;
        }
    }

    private static object Describe(GeolocationListeningRequest request) => new
    {
        desiredAccuracy = request.DesiredAccuracy.ToString(),
        minimumTime = request.MinimumTime.TotalMilliseconds,
    };

    /// <inheritdoc />
    public async Task<bool> StartListeningForegroundAsync(GeolocationListeningRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            if (_listening is not null)
                throw new InvalidOperationException("This Geolocation is already listening.");
            _listening = request;
            // Before the start, so that no location told right after it is missed.
            _changed = Subscribe("locationChanged", OnChanged);
            _failed = Subscribe("listeningFailed", OnFailed);
        }

        try
        {
            var started = await CallAsync("startListening", [Describe(request)], EssentialsCallOptions.Interactive).ConfigureAwait(true);
            if (started.ValueKind == JsonValueKind.True)
                return true;
        }
        catch
        {
            StopListeningCore();
            throw;
        }
        StopListeningCore();
        return false;
    }

    /// <inheritdoc />
    public void StopListeningForeground()
    {
        if (StopListeningCore() && IsAvailable)
            Post("stopListening");
    }

    private bool StopListeningCore()
    {
        lock (_gate)
        {
            if (_listening is null)
                return false;
            _listening = null;
            _changed?.Dispose();
            _failed?.Dispose();
            _changed = _failed = null;
            return true;
        }
    }

    private void OnChanged(JsonElement? payload)
    {
        if (payload is not { } value || ParseLocation(value) is not { } location)
            return;
        _last = location;
        LocationChanged?.Invoke(this, new GeolocationLocationChangedEventArgs(location));
    }

    private void OnFailed(JsonElement? payload)
    {
        var error = payload is { } value ? EssentialsJson.Enum(value, "error", GeolocationError.PositionUnavailable) : GeolocationError.PositionUnavailable;
        if (error == GeolocationError.Unauthorized)
            _enabled = false;
        // MAUI stops listening when it fails.
        StopListeningCore();
        ListeningFailed?.Invoke(this, new GeolocationListeningFailedEventArgs(error));
    }

    internal static Location? ParseLocation(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return null;
        var latitude = EssentialsJson.Double(value, "latitude");
        var longitude = EssentialsJson.Double(value, "longitude");
        if (latitude is not (>= -90 and <= 90) || longitude is not (>= -180 and <= 180))
            return null;
        return new Location(latitude.Value, longitude.Value, EssentialsJson.Time(value, "timestamp") ?? DateTimeOffset.UtcNow)
        {
            Altitude = EssentialsJson.Double(value, "altitude"),
            Accuracy = EssentialsJson.Double(value, "accuracy"),
            VerticalAccuracy = EssentialsJson.Double(value, "verticalAccuracy"),
            Speed = EssentialsJson.Double(value, "speed"),
            Course = EssentialsJson.Double(value, "course"),
            ReducedAccuracy = EssentialsJson.Bool(value, "reducedAccuracy") ?? false,
            IsFromMockProvider = EssentialsJson.Bool(value, "isFromMockProvider") ?? false,
            AltitudeReferenceSystem = EssentialsJson.Enum(value, "altitudeReferenceSystem", AltitudeReferenceSystem.Unspecified),
        };
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        StopListeningCore();
        base.Dispose(disposing);
    }
}

/// <summary>
/// <see cref="IGeocoding"/> of the client. Service <c>geocoding</c>; methods <c>getPlacemarks(latitude, longitude)</c> returning
/// <c>[{ location, countryCode, countryName, featureName, postalCode, subLocality, thoroughfare, subThoroughfare, locality, adminArea, subAdminArea }]</c>
/// and <c>getLocations(address)</c> returning locations.
/// </summary>
public sealed class RemoteGeocoding(RouteableEssentials essentials) : RemoteService(essentials, ServiceName), IGeocoding
{
    internal const string ServiceName = "geocoding";

    /// <inheritdoc />
    public async Task<IEnumerable<Placemark>> GetPlacemarksAsync(double latitude, double longitude)
    {
        var result = await CallAsync("getPlacemarks", [latitude, longitude]).ConfigureAwait(true);
        return [.. EssentialsJson.Array(result, 100).Where(p => p.ValueKind == JsonValueKind.Object).Select(p => new Placemark
        {
            Location = p.TryGetProperty("location", out var l) ? RemoteGeolocation.ParseLocation(l) ?? new Location(latitude, longitude) : new Location(latitude, longitude),
            CountryCode = EssentialsJson.String(p, "countryCode", 8) ?? string.Empty,
            CountryName = EssentialsJson.String(p, "countryName", 256) ?? string.Empty,
            FeatureName = EssentialsJson.String(p, "featureName", 256) ?? string.Empty,
            PostalCode = EssentialsJson.String(p, "postalCode", 32) ?? string.Empty,
            SubLocality = EssentialsJson.String(p, "subLocality", 256) ?? string.Empty,
            Thoroughfare = EssentialsJson.String(p, "thoroughfare", 256) ?? string.Empty,
            SubThoroughfare = EssentialsJson.String(p, "subThoroughfare", 256) ?? string.Empty,
            Locality = EssentialsJson.String(p, "locality", 256) ?? string.Empty,
            AdminArea = EssentialsJson.String(p, "adminArea", 256) ?? string.Empty,
            SubAdminArea = EssentialsJson.String(p, "subAdminArea", 256) ?? string.Empty,
        })];
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Location>> GetLocationsAsync(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var result = await CallAsync("getLocations", [EssentialsJson.Limit(address, 1024)]).ConfigureAwait(true);
        return [.. EssentialsJson.Array(result, 100).Select(RemoteGeolocation.ParseLocation).OfType<Location>()];
    }
}
