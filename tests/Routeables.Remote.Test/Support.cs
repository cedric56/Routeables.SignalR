using System.Security.Claims;

namespace Routeables.Remote.Tests;

/// <summary>A clock the test moves.</summary>
public sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now = start;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>A channel that fails as networks do: down, or losing the answer of a call after the server did the work.</summary>
public sealed class FaultyChannel(IReplicationChannel inner) : IReplicationChannel
{
    public bool Down { get; set; }

    /// <summary>The next push is applied by the server, then its answer is lost.</summary>
    public bool LoseNextPushAnswer { get; set; }

    /// <summary>The next pull answer is lost.</summary>
    public bool LoseNextPullAnswer { get; set; }

    private void Check()
    {
        if (Down)
            throw new HttpRequestException("No network.");
    }

    public Task<RemoteWelcome> HelloAsync(RemoteHello hello, CancellationToken ct = default)
    {
        Check();
        return inner.HelloAsync(hello, ct);
    }

    public async Task<PushResult> PushAsync(PushRequest request, CancellationToken ct = default)
    {
        Check();
        var result = await inner.PushAsync(request, ct);
        if (LoseNextPushAnswer)
        {
            LoseNextPushAnswer = false;
            throw new HttpRequestException("The connection was reset.");
        }
        return result;
    }

    public async Task<PullResult> PullAsync(PullRequest request, CancellationToken ct = default)
    {
        Check();
        var result = await inner.PullAsync(request, ct);
        if (LoseNextPullAnswer)
        {
            LoseNextPullAnswer = false;
            throw new HttpRequestException("The connection was reset.");
        }
        return result;
    }

    public Task<SnapshotResult> SnapshotAsync(SnapshotRequest request, CancellationToken ct = default)
    {
        Check();
        return inner.SnapshotAsync(request, ct);
    }
}

public static class Users
{
    public static ClaimsPrincipal Of(string id) => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], "test"));
}

/// <summary>A device: its replica, its storage (which survives a restart), and its network.</summary>
public sealed class Device
{
    private Device(string name, Replica replica, IReplicaStorage storage, FaultyChannel network, ReplicationServer server, ClaimsPrincipal user,
        string[] collections, TimeProvider? time)
    {
        Name = name;
        Replica = replica;
        Storage = storage;
        Network = network;
        Server = server;
        User = user;
        Collections = collections;
        Time = time;
    }

    public string Name { get; }

    public Replica Replica { get; private set; }

    public IReplicaStorage Storage { get; }

    public FaultyChannel Network { get; private set; }

    public ReplicationServer Server { get; private set; }

    /// <summary>The server process was restarted: the device reaches the new one.</summary>
    public void ConnectTo(ReplicationServer server)
    {
        Server = server;
        Network = new FaultyChannel(server.Connect(User));
    }

    public ClaimsPrincipal User { get; }

    public string[] Collections { get; }

    public TimeProvider? Time { get; }

    public static async Task<Device> StartAsync(string name, ReplicationServer server, string user = "user", string[]? collections = null,
        TimeProvider? time = null)
    {
        var storage = new MemoryReplicaStorage();
        var principal = Users.Of(user);
        collections ??= ["Person"];
        var replica = await Replica.OpenAsync(storage, collections, time);
        return new Device(name, replica, storage, new FaultyChannel(server.Connect(principal)), server, principal, collections, time);
    }

    /// <summary>The process stops and starts again: only what was stored is left.</summary>
    public async Task RestartAsync() => Replica = await Replica.OpenAsync(Storage, Collections, Time);

    public Task<SyncReport> SyncAsync() => Replica.SyncAsync(Network);

    /// <summary>Synchronizes, ignoring a failure of the network (the next one goes on).</summary>
    public async Task<bool> TrySyncAsync()
    {
        try
        {
            await SyncAsync();
            return true;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }
}

/// <summary>What the tests read of a server in memory: its store completes at once.</summary>
public static class ServerExtensions
{
    extension(ReplicationServer server)
    {
        public DocumentState? Find(DocumentId id) => server.FindAsync(id).AsTask().GetAwaiter().GetResult();

        public long Position => server.Store.HeadAsync().AsTask().GetAwaiter().GetResult().Position;

        public IReadOnlyList<LogEntry> Log => ((MemoryReplicationStore)server.Store).Log;
    }
}
