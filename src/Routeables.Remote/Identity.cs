using System.Text.Json;
using System.Text.Json.Serialization;

namespace Routeables.Remote;

/// <summary>
/// A replica: one installation of a client (a device, a browser profile), or the server. Stable for the life of its
/// local state: a client whose state is lost takes a new one.
/// </summary>
/// <remarks>
/// On the wire, its text in lower case ("4f1c2a9e-..."). Replicas are ordered as those texts are, ordinally: the order
/// every client (.NET, TypeScript) uses to break ties between concurrent writes.
/// </remarks>
[JsonConverter(typeof(ReplicaIdJsonConverter))]
public readonly record struct ReplicaId(Guid Value) : IComparable<ReplicaId>
{
    public static ReplicaId New() => new(Guid.NewGuid());

    public static ReplicaId Parse(string text) => new(Guid.Parse(text));

    // The bytes in big-endian order are the order of the text, digit by digit.
    public int CompareTo(ReplicaId other)
    {
        Span<byte> left = stackalloc byte[16];
        Span<byte> right = stackalloc byte[16];
        Value.TryWriteBytes(left, bigEndian: true, out _);
        other.Value.TryWriteBytes(right, bigEndian: true, out _);
        return left.SequenceCompareTo(right);
    }

    public override string ToString() => Value.ToString("D");
}

/// <summary>A <see cref="ReplicaId"/> as its text.</summary>
public sealed class ReplicaIdJsonConverter : JsonConverter<ReplicaId>
{
    public override ReplicaId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.String && Guid.TryParse(reader.GetString(), out var value)
            ? new ReplicaId(value)
            : throw new JsonException("A replica is the text of a GUID.");

    public override void Write(Utf8JsonWriter writer, ReplicaId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value.ToString("D"));
}

/// <summary>
/// The identity of a change: its replica and its rank among the changes of that replica (1, 2, 3...). Each replica numbers
/// its changes without gaps, so that "everything of R up to n" describes what was received, and a change sent twice is
/// recognized.
/// </summary>
public readonly record struct Dot(ReplicaId Replica, long Seq)
{
    public override string ToString() => $"{Replica}:{Seq}";
}

/// <summary>
/// A hybrid logical clock value: wall time in milliseconds, a counter for events within the same millisecond (or while
/// the wall clock lags behind a value already seen), and the replica, which breaks the remaining ties. The order is total
/// and the same on every replica: two concurrent writes of one field keep the same winner everywhere.
/// </summary>
public readonly record struct Hlc(long Wall, int Counter, ReplicaId Replica) : IComparable<Hlc>
{
    public int CompareTo(Hlc other)
    {
        var wall = Wall.CompareTo(other.Wall);
        if (wall != 0)
            return wall;
        var counter = Counter.CompareTo(other.Counter);
        return counter != 0 ? counter : Replica.CompareTo(other.Replica);
    }

    public static bool operator <(Hlc left, Hlc right) => left.CompareTo(right) < 0;

    public static bool operator >(Hlc left, Hlc right) => left.CompareTo(right) > 0;

    public static bool operator <=(Hlc left, Hlc right) => left.CompareTo(right) <= 0;

    public static bool operator >=(Hlc left, Hlc right) => left.CompareTo(right) >= 0;

    public override string ToString() => $"{Wall}.{Counter}@{Replica}";
}

/// <summary>
/// The clock of a replica (Kulkarni et al., "Logical Physical Clocks", 2014). It never goes back, even when the wall clock
/// does, and moves past every value it observes, so that a change made after seeing another one is stamped after it.
/// </summary>
public sealed class HybridClock
{
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private Hlc _last;
    // Added to the time of the device, in milliseconds: zero, or negative when the device's clock was found ahead of the server's.
    private long _offset;

    /// <summary>
    /// How far ahead of the server the device's clock may be before it is corrected (<see cref="Calibrate"/>): beyond the delay of a
    /// round trip, well within the tolerance of a server (<c>MaxClockSkew</c>, 5 minutes by default). One minute.
    /// </summary>
    public static TimeSpan CalibrationThreshold { get; } = TimeSpan.FromMinutes(1);

    public HybridClock(ReplicaId replica, TimeProvider? time = null, Hlc? last = null)
    {
        Replica = replica;
        _time = time ?? TimeProvider.System;
        _last = last ?? new Hlc(0, 0, replica);
    }

    public ReplicaId Replica { get; }

    /// <summary>The last value given or observed.</summary>
    public Hlc Last
    {
        get
        {
            lock (_gate)
                return _last;
        }
    }

    private long DeviceNow => _time.GetUtcNow().ToUnixTimeMilliseconds();

    private long WallNow => DeviceNow + _offset;

    /// <summary>
    /// What is added to the time of the device to stamp: <see cref="TimeSpan.Zero"/>, or a negative correction when the device's
    /// clock was found ahead of the server's (<see cref="Calibrate"/>).
    /// </summary>
    public TimeSpan Offset
    {
        get
        {
            lock (_gate)
                return TimeSpan.FromMilliseconds(_offset);
        }
    }

    /// <summary>
    /// Compares the time of the device with <paramref name="server"/>, the clock of the server just received: when the device is ahead
    /// by more than <see cref="CalibrationThreshold"/> (or at all, with <paramref name="force"/>, after the server refused a change as
    /// stamped in the future), the next values are stamped by the time of the server, measured through the device's own clock. Only a
    /// clock ahead is corrected: one behind is harmless, the clock follows the values it observes. Returns whether a correction is in effect.
    /// </summary>
    public bool Calibrate(Hlc server, bool force = false)
    {
        if (server.Wall <= 0)
            return Offset != TimeSpan.Zero;     // a server that gave no time
        lock (_gate)
        {
            var ahead = DeviceNow - server.Wall;
            _offset = ahead > (force ? 0 : (long)CalibrationThreshold.TotalMilliseconds) ? -ahead : 0;
            return _offset != 0;
        }
    }

    /// <summary>
    /// The value right after (<paramref name="wall"/>, <paramref name="counter"/>): the next counter, or the next millisecond when the counter
    /// is used up. A counter never wraps around: a value that did would come before every value of its millisecond.
    /// </summary>
    internal static (long Wall, int Counter) After(long wall, int counter) => counter >= int.MaxValue ? (wall + 1, 0) : (wall, counter + 1);

    /// <summary>A new value, after every value given or observed so far.</summary>
    public Hlc Next()
    {
        lock (_gate)
        {
            var wall = Math.Max(WallNow, _last.Wall);
            if (wall == _last.Wall)
            {
                var (after, counter) = After(wall, _last.Counter);
                _last = new Hlc(after, counter, Replica);
            }
            else
            {
                _last = new Hlc(wall, 0, Replica);
            }
            return _last;
        }
    }

    /// <summary>
    /// Falls back from a clock that ran ahead (the device's time was wrong) to the later of the time now and
    /// <paramref name="floor"/>, the clock of the server. The only way a hybrid clock goes back: used when the server refuses
    /// a change as stamped in the future, never otherwise.
    /// </summary>
    public void FallBack(Hlc floor)
    {
        lock (_gate)
        {
            var wall = Math.Max(WallNow, floor.Wall);
            if (wall < _last.Wall)
                _last = new Hlc(wall, wall == floor.Wall ? floor.Counter : 0, Replica);
        }
    }

    /// <summary>
    /// A value received from another replica: the next values come after it. A negative counter is never sent by a replica: such a value
    /// is not followed.
    /// </summary>
    public void Observe(Hlc remote)
    {
        if (remote.Counter < 0)
            return;
        lock (_gate)
        {
            var wall = Math.Max(WallNow, Math.Max(_last.Wall, remote.Wall));
            if (wall != _last.Wall && wall != remote.Wall)
            {
                _last = new Hlc(wall, 0, Replica);
                return;
            }
            var counter = wall == _last.Wall && wall == remote.Wall ? Math.Max(_last.Counter, remote.Counter)
                : wall == _last.Wall ? _last.Counter
                : remote.Counter;
            var (after, next) = After(wall, counter);
            _last = new Hlc(after, next, Replica);
        }
    }
}
