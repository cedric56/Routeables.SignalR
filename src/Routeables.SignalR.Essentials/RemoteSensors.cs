using System.Text.Json;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Devices.Sensors;

namespace Routeables.SignalR.Essentials;

/// <summary>
/// The sensors of the client. Each one is a service (<c>accelerometer</c>, <c>barometer</c>, <c>compass</c>, <c>gyroscope</c>,
/// <c>magnetometer</c>, <c>orientationSensor</c>) with the state <c>{ isSupported }</c>, the methods <c>start({ speed })</c> and
/// <c>stop()</c>, and the event <c>reading</c>. A sensor that runs survives a reconnection: it is started again when the client announces
/// itself. The client spaces the readings it sends (a network is not a sensor bus): <c>Fastest</c> is not faster than it allows.
/// </summary>
public abstract class RemoteSensor : RemoteService
{
    private readonly object _gate = new();
    private IDisposable? _readings;
    private object? _start;
    private bool _supported;

    private protected RemoteSensor(RouteableEssentials essentials, string service) : base(essentials, service)
    {
    }

    /// <summary>Whether the device of the user has the sensor.</summary>
    public bool IsSupported => _supported;

    /// <summary>Whether the sensor runs.</summary>
    public bool IsMonitoring
    {
        get { lock (_gate) return _start is not null; }
    }

    /// <inheritdoc />
    protected override void OnReady()
    {
        _supported = TryGetState(out var s) && (EssentialsJson.Bool(s, "isSupported") ?? false);
        object? start;
        lock (_gate)
            start = _start;
        if (start is not null && _supported)
            Post("start", [start]);
    }

    private protected void StartCore(SensorSpeed speed, object? extra = null)
    {
        // Before the client announced itself the sensor is started with the announcement (OnReady).
        if (Essentials.HasAnnounced && !_supported)
            throw new FeatureNotSupportedException($"The device of this user has no {Service}.");
        lock (_gate)
        {
            if (_start is not null)
                throw new InvalidOperationException($"The {Service} is already being monitored.");
            _start = extra ?? new { speed = speed.ToString() };
            _readings = Subscribe("reading", payload =>
            {
                if (payload is { ValueKind: JsonValueKind.Object } reading && IsMonitoring)
                    OnReading(reading);
            });
        }
        if (Essentials.HasAnnounced)
            Post("start", [_start]);
    }

    /// <summary>Stops the sensor.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (_start is null)
                return;
            _start = null;
            _readings?.Dispose();
            _readings = null;
        }
        if (IsAvailable)
            Post("stop");
    }

    private protected abstract void OnReading(JsonElement reading);

    private protected static double Value(JsonElement reading, string name) => EssentialsJson.Double(reading, name) ?? 0;

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        lock (_gate)
        {
            _start = null;
            _readings?.Dispose();
            _readings = null;
        }
        base.Dispose(disposing);
    }
}

/// <summary><see cref="IAccelerometer"/> of the client: readings <c>{ x, y, z }</c> in g. <see cref="ShakeDetected"/> is computed on the server, as MAUI does.</summary>
public sealed class RemoteAccelerometer : RemoteSensor, IAccelerometer
{
    internal const string ServiceName = "accelerometer";
    private readonly ShakeDetector _shake = new();

    /// <summary>Creates the accelerometer of a session.</summary>
    public RemoteAccelerometer(RouteableEssentials essentials) : base(essentials, ServiceName) => Initialize();

    /// <inheritdoc />
    public event EventHandler<AccelerometerChangedEventArgs>? ReadingChanged;

    /// <inheritdoc />
    public event EventHandler? ShakeDetected;

    /// <inheritdoc />
    public void Start(SensorSpeed sensorSpeed) => StartCore(sensorSpeed);

    private protected override void OnReading(JsonElement reading)
    {
        var data = new AccelerometerData(Value(reading, "x"), Value(reading, "y"), Value(reading, "z"));
        ReadingChanged?.Invoke(this, new AccelerometerChangedEventArgs(data));
        if (ShakeDetected is not null && _shake.Add(DateTime.UtcNow, data.Acceleration))
            ShakeDetected?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// The shake detection of MAUI (AccelerometerQueue): a shake is when at least three quarters of the readings of the last half second, over at
/// least a quarter of a second and four readings, exceed 13 m/s².
/// </summary>
internal sealed class ShakeDetector
{
    private const double Gravity = 9.81;
    private const double Threshold = 169;   // (13 m/s²)²
    private static readonly TimeSpan MaxWindow = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MinWindow = TimeSpan.FromMilliseconds(250);
    private const int MinSamples = 4;

    private readonly Queue<(DateTime Time, bool Accelerating)> _samples = new();
    private int _accelerating;

    public bool Add(DateTime now, System.Numerics.Vector3 acceleration)
    {
        var x = acceleration.X * Gravity;
        var y = acceleration.Y * Gravity;
        var z = acceleration.Z * Gravity;
        var accelerating = x * x + y * y + z * z > Threshold;

        while (_samples.Count > 0 && _samples.Peek().Time < now - MaxWindow)
            if (_samples.Dequeue().Accelerating)
                _accelerating--;
        _samples.Enqueue((now, accelerating));
        if (accelerating)
            _accelerating++;

        var shaking = _samples.Count >= MinSamples
            && now - _samples.Peek().Time >= MinWindow
            && _accelerating >= (_samples.Count >> 1) + (_samples.Count >> 2);
        if (shaking)
        {
            _samples.Clear();
            _accelerating = 0;
        }
        return shaking;
    }
}

/// <summary><see cref="IBarometer"/> of the client: readings <c>{ pressure }</c> in hectopascals.</summary>
public sealed class RemoteBarometer : RemoteSensor, IBarometer
{
    internal const string ServiceName = "barometer";

    /// <summary>Creates the barometer of a session.</summary>
    public RemoteBarometer(RouteableEssentials essentials) : base(essentials, ServiceName) => Initialize();

    /// <inheritdoc />
    public event EventHandler<BarometerChangedEventArgs>? ReadingChanged;

    /// <inheritdoc />
    public void Start(SensorSpeed sensorSpeed) => StartCore(sensorSpeed);

    private protected override void OnReading(JsonElement reading)
        => ReadingChanged?.Invoke(this, new BarometerChangedEventArgs(new BarometerData(Value(reading, "pressure"))));
}

/// <summary><see cref="ICompass"/> of the client: readings <c>{ heading }</c> in degrees from the magnetic north.</summary>
public sealed class RemoteCompass : RemoteSensor, ICompass
{
    internal const string ServiceName = "compass";

    /// <summary>Creates the compass of a session.</summary>
    public RemoteCompass(RouteableEssentials essentials) : base(essentials, ServiceName) => Initialize();

    /// <inheritdoc />
    public event EventHandler<CompassChangedEventArgs>? ReadingChanged;

    /// <inheritdoc />
    public void Start(SensorSpeed sensorSpeed) => Start(sensorSpeed, applyLowPassFilter: false);

    /// <inheritdoc />
    public void Start(SensorSpeed sensorSpeed, bool applyLowPassFilter)
        => StartCore(sensorSpeed, new { speed = sensorSpeed.ToString(), applyLowPassFilter });

    private protected override void OnReading(JsonElement reading)
    {
        var heading = Value(reading, "heading") % 360;
        ReadingChanged?.Invoke(this, new CompassChangedEventArgs(new CompassData(heading < 0 ? heading + 360 : heading)));
    }
}

/// <summary><see cref="IGyroscope"/> of the client: readings <c>{ x, y, z }</c> in radians per second.</summary>
public sealed class RemoteGyroscope : RemoteSensor, IGyroscope
{
    internal const string ServiceName = "gyroscope";

    /// <summary>Creates the gyroscope of a session.</summary>
    public RemoteGyroscope(RouteableEssentials essentials) : base(essentials, ServiceName) => Initialize();

    /// <inheritdoc />
    public event EventHandler<GyroscopeChangedEventArgs>? ReadingChanged;

    /// <inheritdoc />
    public void Start(SensorSpeed sensorSpeed) => StartCore(sensorSpeed);

    private protected override void OnReading(JsonElement reading)
        => ReadingChanged?.Invoke(this, new GyroscopeChangedEventArgs(new GyroscopeData(Value(reading, "x"), Value(reading, "y"), Value(reading, "z"))));
}

/// <summary><see cref="IMagnetometer"/> of the client: readings <c>{ x, y, z }</c> in microteslas.</summary>
public sealed class RemoteMagnetometer : RemoteSensor, IMagnetometer
{
    internal const string ServiceName = "magnetometer";

    /// <summary>Creates the magnetometer of a session.</summary>
    public RemoteMagnetometer(RouteableEssentials essentials) : base(essentials, ServiceName) => Initialize();

    /// <inheritdoc />
    public event EventHandler<MagnetometerChangedEventArgs>? ReadingChanged;

    /// <inheritdoc />
    public void Start(SensorSpeed sensorSpeed) => StartCore(sensorSpeed);

    private protected override void OnReading(JsonElement reading)
        => ReadingChanged?.Invoke(this, new MagnetometerChangedEventArgs(new MagnetometerData(Value(reading, "x"), Value(reading, "y"), Value(reading, "z"))));
}

/// <summary><see cref="IOrientationSensor"/> of the client: readings <c>{ x, y, z, w }</c>, a quaternion.</summary>
public sealed class RemoteOrientationSensor : RemoteSensor, IOrientationSensor
{
    internal const string ServiceName = "orientationSensor";

    /// <summary>Creates the orientation sensor of a session.</summary>
    public RemoteOrientationSensor(RouteableEssentials essentials) : base(essentials, ServiceName) => Initialize();

    /// <inheritdoc />
    public event EventHandler<OrientationSensorChangedEventArgs>? ReadingChanged;

    /// <inheritdoc />
    public void Start(SensorSpeed sensorSpeed) => StartCore(sensorSpeed);

    private protected override void OnReading(JsonElement reading)
        => ReadingChanged?.Invoke(this, new OrientationSensorChangedEventArgs(
            new OrientationSensorData(Value(reading, "x"), Value(reading, "y"), Value(reading, "z"), Value(reading, "w"))));
}
