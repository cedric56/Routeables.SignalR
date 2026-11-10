using System.Text.Json;
using Microsoft.Maui.Networking;

namespace Routeables.SignalR.Essentials;

/// <summary>
/// <see cref="IConnectivity"/> of the client of a session: <see cref="NetworkAccess"/> and <see cref="ConnectionProfiles"/> are the last ones
/// the client reported (with <see cref="EssentialsReady"/>, then at each change), read on the server without a round trip.
/// <see cref="ConnectivityChanged"/> is raised on the dispatcher of the session.
/// </summary>
/// <remarks>
/// Service <c>connectivity</c>. State, and payload of the event <c>changed</c>: <c>{ "networkAccess": "Internet", "profiles": ["WiFi"] }</c>,
/// with the names of the MAUI enums <c>NetworkAccess</c> and <c>ConnectionProfile</c>. While the client has said
/// nothing, or does not support the service, the access is <c>Unknown</c>. A browser reports the network of the device:
/// while its connection to the server is gone, the server hears nothing.
/// </remarks>
public sealed class RemoteConnectivity : IConnectivity, IDisposable
{
    internal const string Service = "connectivity";
    internal const string ChangedEvent = "changed";

    private readonly RouteableEssentials _essentials;
    private readonly IDisposable _subscription;
    private volatile Snapshot _current = Snapshot.Unknown;

    internal sealed record Snapshot(NetworkAccess Access, IReadOnlyList<ConnectionProfile> Profiles)
    {
        public static readonly Snapshot Unknown = new(NetworkAccess.Unknown, []);

        public bool SameAs(Snapshot other) => Access == other.Access && Profiles.SequenceEqual(other.Profiles);
    }

    /// <summary>Creates the connectivity of a session.</summary>
    public RemoteConnectivity(RouteableEssentials essentials)
    {
        _essentials = essentials ?? throw new ArgumentNullException(nameof(essentials));
        _subscription = essentials.Subscribe(Service, ChangedEvent, payload => Update(Parse(payload), raise: true));
        essentials.Ready += Load;
        if (essentials.HasAnnounced)
            Load();
    }

    /// <inheritdoc />
    public NetworkAccess NetworkAccess => _current.Access;

    /// <inheritdoc />
    public IEnumerable<ConnectionProfile> ConnectionProfiles => _current.Profiles;

    /// <inheritdoc />
    public event EventHandler<ConnectivityChangedEventArgs>? ConnectivityChanged;

    // The state of a new announcement: a client that comes back may be on another network.
    private void Load()
        => Update(_essentials.IsSupported(Service) && _essentials.TryGetState(Service, out var state) ? Parse(state) : Snapshot.Unknown, raise: true);

    private void Update(Snapshot next, bool raise)
    {
        var previous = _current;
        _current = next;
        if (raise && !previous.SameAs(next))
            ConnectivityChanged?.Invoke(this, new ConnectivityChangedEventArgs(next.Access, next.Profiles));
    }

    internal static Snapshot Parse(JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } value)
            return Snapshot.Unknown;

        var access = value.TryGetProperty("networkAccess", out var a) && a.ValueKind == JsonValueKind.String
            && Enum.TryParse<NetworkAccess>(a.GetString(), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : NetworkAccess.Unknown;

        var profiles = new List<ConnectionProfile>();
        if (value.TryGetProperty("profiles", out var p) && p.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in p.EnumerateArray().Take(8))
                if (item.ValueKind == JsonValueKind.String
                    && Enum.TryParse<ConnectionProfile>(item.GetString(), ignoreCase: true, out var profile) && Enum.IsDefined(profile)
                    && !profiles.Contains(profile))
                    profiles.Add(profile);
        }
        return new Snapshot(access, profiles);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _essentials.Ready -= Load;
        _subscription.Dispose();
    }
}
