namespace Routeables.Remote.Tests;

/// <summary>Replicas that write offline, concurrently, and converge once they synchronize, whatever the order.</summary>
public class ConvergenceTests
{
    private static readonly DocumentId Ana = new("Person", "ana");

    private static string Fingerprint(DocumentState? state)
        => state is null ? "∅"
            : $"{(state.Exists ? "+" : "-")} " + string.Join(" ", state.RegisterNames.Order(StringComparer.Ordinal).Select(f => $"{f}={state.Get(f)}"))
              + " | " + string.Join(" ", state.SetNames.Order(StringComparer.Ordinal).Select(f => $"{f}=[{string.Join(",", state.Elements(f))}]"))
              + " | " + string.Join(" ", state.CounterNames.Order(StringComparer.Ordinal).Select(f => $"{f}={state.Counter(f)}"));

    [Fact]
    public async Task Two_devices_offline_converge_and_a_concurrent_addition_survives_a_removal()
    {
        var time = new ManualTime(DateTimeOffset.UnixEpoch.AddDays(20_000));
        var server = new ReplicationServer(AllowAllReplicationPolicy.Instance, time: time);
        var a = await Device.StartAsync("A", server, time: time);
        var b = await Device.StartAsync("B", server, time: time);

        // A creates Ana with the tag "shared"; B receives it.
        await a.Replica.Edit().Create(Ana).Set(Ana, "name", "\"Ana\"").AddElement(Ana, "tags", "\"shared\"").CommitAsync();
        await a.SyncAsync();
        await b.SyncAsync();
        Assert.Equal(["\"shared\""], b.Replica.Find(Ana)!.Elements("tags"));

        // Both offline: A renames and removes the tag; B renames later and adds the tag again.
        a.Network.Down = b.Network.Down = true;
        await a.Replica.Edit().Set(Ana, "name", "\"Alice\"").RemoveElement(Ana, "tags", "\"shared\"").CommitAsync();
        time.Now = time.Now.AddSeconds(1);
        await b.Replica.Edit().Set(Ana, "name", "\"Bob\"").AddElement(Ana, "tags", "\"shared\"").CommitAsync();
        Assert.False(await a.TrySyncAsync());
        Assert.Empty(a.Replica.Find(Ana)!.Elements("tags"));          // offline, A sees its own removal at once

        // The network is back, in any order.
        a.Network.Down = b.Network.Down = false;
        await b.SyncAsync();
        await a.SyncAsync();
        await b.SyncAsync();

        var expected = Fingerprint(server.Find(Ana));
        Assert.Equal(expected, Fingerprint(a.Replica.Find(Ana)));
        Assert.Equal(expected, Fingerprint(b.Replica.Find(Ana)));
        Assert.Equal("\"Bob\"", server.Find(Ana)!.Get("name"));        // B wrote after A: the latest stamp wins
        Assert.Equal(["\"shared\""], server.Find(Ana)!.Elements("tags")); // B's addition was not seen by A's removal: it stays
        Assert.Equal(0, a.Replica.PendingCount);
        Assert.Equal(0, b.Replica.PendingCount);
    }

    [Fact]
    public async Task Concurrent_writes_of_one_field_have_the_same_winner_whatever_the_order_of_synchronization()
    {
        var time = new ManualTime(DateTimeOffset.UnixEpoch.AddDays(20_000));
        foreach (var order in new[] { "ab", "ba" })
        {
            var server = new ReplicationServer(AllowAllReplicationPolicy.Instance, time: time);
            var a = await Device.StartAsync("A", server, time: time);
            var b = await Device.StartAsync("B", server, time: time);
            await a.Replica.Edit().Create(Ana).Set(Ana, "name", "\"from A\"").CommitAsync();
            await b.Replica.Edit().Create(Ana).Set(Ana, "name", "\"from B\"").CommitAsync();    // same millisecond: the replica decides

            foreach (var device in order == "ab" ? new[] { a, b, a } : [b, a, b])
                await device.SyncAsync();

            var winner = server.Find(Ana)!.Get("name");
            Assert.Equal(winner, a.Replica.Find(Ana)!.Get("name"));
            Assert.Equal(winner, b.Replica.Find(Ana)!.Get("name"));
            var expected = a.Replica.Id.CompareTo(b.Replica.Id) > 0 ? "\"from A\"" : "\"from B\"";
            Assert.Equal(expected, winner);
        }
    }

    [Fact]
    public async Task Concurrent_increments_add_up()
    {
        var server = new ReplicationServer(AllowAllReplicationPolicy.Instance);
        var devices = new List<Device>();
        for (var i = 0; i < 3; i++)
            devices.Add(await Device.StartAsync($"D{i}", server));
        await devices[0].Replica.Edit().Create(Ana).CommitAsync();
        await devices[0].SyncAsync();

        foreach (var device in devices)
        {
            await device.SyncAsync();
            device.Network.Down = true;
            for (var i = 0; i < 5; i++)
                await device.Replica.Edit().Increment(Ana, "visits", 1).CommitAsync();
        }
        foreach (var device in devices)
        {
            device.Network.Down = false;
            await device.SyncAsync();
        }
        foreach (var device in devices)
            await device.SyncAsync();

        Assert.Equal(15, server.Find(Ana)!.Counter("visits"));
        Assert.All(devices, d => Assert.Equal(15, d.Replica.Find(Ana)!.Counter("visits")));
    }

    [Fact]
    public async Task A_deletion_and_a_concurrent_restoration_have_one_outcome_everywhere()
    {
        var time = new ManualTime(DateTimeOffset.UnixEpoch.AddDays(20_000));
        var server = new ReplicationServer(AllowAllReplicationPolicy.Instance, time: time);
        var a = await Device.StartAsync("A", server, time: time);
        var b = await Device.StartAsync("B", server, time: time);
        await a.Replica.Edit().Create(Ana).Set(Ana, "name", "\"Ana\"").CommitAsync();
        await a.SyncAsync();
        await b.SyncAsync();

        await a.Replica.Edit().Delete(Ana).CommitAsync();
        time.Now = time.Now.AddSeconds(1);
        await b.Replica.Edit().Create(Ana).Set(Ana, "name", "\"Ana again\"").CommitAsync();     // later: restores it
        await a.SyncAsync();
        await b.SyncAsync();
        await a.SyncAsync();

        Assert.True(server.Find(Ana)!.Exists);
        Assert.Equal(Fingerprint(server.Find(Ana)), Fingerprint(a.Replica.Find(Ana)));
        Assert.Equal(Fingerprint(server.Find(Ana)), Fingerprint(b.Replica.Find(Ana)));
    }

    [Fact]
    public async Task Random_writes_cuts_restarts_and_lost_answers_always_converge()
    {
        for (var seed = 1; seed <= 20; seed++)
            await RunAsync(seed);
    }

    private static async Task RunAsync(int seed)
    {
        var random = new Random(seed);
        var server = new ReplicationServer(AllowAllReplicationPolicy.Instance);
        var devices = new List<Device>();
        for (var i = 0; i < 4; i++)
            devices.Add(await Device.StartAsync($"D{i}", server));
        var documents = Enumerable.Range(0, 5).Select(i => new DocumentId("Person", $"p{i}")).ToArray();
        long increments = 0;
        var commits = 0;

        for (var step = 0; step < 400; step++)
        {
            var device = devices[random.Next(devices.Count)];
            var document = documents[random.Next(documents.Length)];
            switch (random.Next(10))
            {
                case 0:
                    device.Network.Down = !device.Network.Down;
                    break;
                case 1:
                    device.Network.LoseNextPushAnswer = true;
                    await device.TrySyncAsync();
                    break;
                case 2:
                    device.Network.LoseNextPullAnswer = true;
                    await device.TrySyncAsync();
                    break;
                case 3:
                    await device.TrySyncAsync();
                    break;
                case 4:
                    await device.RestartAsync();      // only what was stored survives
                    break;
                case 5:
                    await device.Replica.Edit().Increment(document, "count", 1).CommitAsync();
                    increments++;
                    commits++;
                    break;
                case 6:
                    await device.Replica.Edit().AddElement(document, "tags", $"t{random.Next(3)}").CommitAsync();
                    commits++;
                    break;
                case 7:
                    await device.Replica.Edit().RemoveElement(document, "tags", $"t{random.Next(3)}").CommitAsync();
                    commits++;
                    break;
                case 8:
                    await device.Replica.Edit().Create(document).Set(document, "name", $"n{random.Next(100)}").CommitAsync();
                    commits++;
                    break;
                default:
                    await device.Replica.Edit().Delete(document).CommitAsync();
                    commits++;
                    break;
            }
        }

        // Every network back, nothing lost any more: two rounds, so that each device sees what the others sent.
        foreach (var device in devices)
        {
            device.Network.Down = false;
            device.Network.LoseNextPushAnswer = device.Network.LoseNextPullAnswer = false;
        }
        for (var round = 0; round < 2; round++)
            foreach (var device in devices)
                await device.SyncAsync();

        foreach (var document in documents)
        {
            var expected = Fingerprint(server.Find(document));
            Assert.All(devices, d => Assert.Equal(expected, Fingerprint(d.Replica.Find(document))));
        }
        Assert.All(devices, d => Assert.Equal(0, d.Replica.PendingCount));
        Assert.Equal(increments, documents.Sum(d => server.Find(d)?.Counter("count") ?? 0));   // each increment counted once
        Assert.Equal(server.Log.Select(e => e.Change.Dot).Distinct().Count(), server.Log.Count);    // no change logged twice
        Assert.Equal(commits, server.Log.Count);                                                    // and none lost
        Assert.All(devices, d => Assert.Empty(d.Replica.Rejected));
    }
}
