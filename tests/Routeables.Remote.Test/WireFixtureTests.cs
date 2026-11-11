using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Routeables.Remote.Tests;

/// <summary>
/// clients/fixtures/remote-v1.json: a log written by this implementation, the documents it merges into, and what each one
/// shows. The TypeScript and Dart clients merge the same log and must show the same: one protocol, three languages.
/// </summary>
public class WireFixtureTests
{
    // RRP_FIXTURE, else the path the project gave at build time (see the csproj: [CallerFilePath] is /_/... in a deterministic CI build).
    private static string FixturePath()
        => Environment.GetEnvironmentVariable("RRP_FIXTURE")
           ?? typeof(WireFixtureTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "RrpFixture").Value!;

    /// <summary>What a document shows, field by field, sorted: compared as JSON across languages.</summary>
    internal static JsonObject Fingerprint(DocumentState state)
    {
        JsonObject Sorted<T>(IEnumerable<string> names, Func<string, T> value)
        {
            var o = new JsonObject();
            foreach (var name in names.Order(StringComparer.Ordinal))
                o[name] = JsonValue.Create(value(name));
            return o;
        }

        var elements = new JsonObject();
        foreach (var name in state.SetNames.Order(StringComparer.Ordinal))
            elements[name] = new JsonArray([.. state.Elements(name).Select(e => (JsonNode)JsonValue.Create(e)!)]);
        return new JsonObject
        {
            ["collection"] = state.Id.Collection,
            ["key"] = state.Id.Key,
            ["exists"] = state.Exists,
            ["registers"] = Sorted(state.RegisterNames, state.Get),
            ["elements"] = elements,
            ["counters"] = Sorted(state.CounterNames, state.Counter),
            ["texts"] = Sorted(state.TextNames, state.Text),
        };
    }

    private static JsonArray Fingerprints(IEnumerable<DocumentState> documents)
        => new([.. documents.OrderBy(d => d.Id.Collection, StringComparer.Ordinal).ThenBy(d => d.Id.Key, StringComparer.Ordinal).Select(d => (JsonNode)Fingerprint(d))]);

    [Fact]
    public async Task This_implementation_merges_the_shared_log_as_recorded()
    {
        var path = FixturePath();
        if (Environment.GetEnvironmentVariable("RRP_WRITE_FIXTURE") == "1")
            await File.WriteAllTextAsync(path, await GenerateAsync());

        var fixture = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        Assert.Equal(RemoteProtocol.Version, (int)fixture["protocol"]!);
        var log = JsonSerializer.Deserialize(fixture["log"]!.ToJsonString(), RemoteJsonContext.Default.PullResult)!;
        var snapshot = JsonSerializer.Deserialize(fixture["snapshot"]!.ToJsonString(), RemoteJsonContext.Default.SnapshotResult)!;
        var expected = fixture["expected"]!.ToJsonString();

        var merged = new DocumentSet();
        foreach (var entry in log.Entries)
            merged.Apply(entry.Change);
        Assert.Equal(expected, Fingerprints(merged.Documents).ToJsonString());
        Assert.Equal(expected, Fingerprints(snapshot.Documents.Select(DocumentState.FromSnapshot)).ToJsonString());

        // The messages read back are written as they were: no field lost, renamed or added.
        Assert.Equal(fixture["log"]!.ToJsonString(), JsonSerializer.Serialize(log, RemoteJsonContext.Default.PullResult));
    }

    /// <summary>Three devices, every kind of operation, offline periods, same-millisecond writes: the log the server keeps.</summary>
    private static async Task<string> GenerateAsync()
    {
        var random = new Random(56);
        var time = new ManualTime(DateTimeOffset.UnixEpoch.AddDays(20_000));
        var server = new ReplicationServer(AllowAllReplicationPolicy.Instance, time: time);
        string[] collections = ["Person", "Note"];
        var devices = new List<Device>();
        for (var i = 0; i < 3; i++)
            devices.Add(await Device.StartAsync($"D{i}", server, collections: collections, time: time));
        var people = Enumerable.Range(0, 3).Select(i => new DocumentId("Person", $"p{i}")).ToArray();
        var note = new DocumentId("Note", "n0");
        await devices[0].Replica.Edit().Create(note).InsertText(note, "body", 0, "Hello").CommitAsync();

        for (var step = 0; step < 160; step++)
        {
            if (random.Next(4) == 0)
                time.Now = time.Now.AddMilliseconds(random.Next(2));     // often the same millisecond: ties
            var device = devices[random.Next(devices.Count)];
            var person = people[random.Next(people.Length)];
            var text = device.Replica.Find(note)?.Text("body") ?? string.Empty;
            var edit = device.Replica.Edit();
            switch (random.Next(11))
            {
                case 0: await device.TrySyncAsync(); continue;
                case 1: device.Network.Down = !device.Network.Down; continue;
                case 2: edit.Create(person).Set(person, "name", $"\"n{random.Next(50)}\""); break;
                case 3: edit.Set(person, "city", random.Next(4) == 0 ? null : $"\"c{random.Next(5)}\""); break;
                case 4: edit.AddElement(person, "tags", $"\"t{random.Next(3)}\""); break;
                case 5: edit.RemoveElement(person, "tags", $"\"t{random.Next(3)}\""); break;
                case 6: edit.Increment(person, "visits", random.Next(-2, 5)); break;
                case 7: edit.Delete(person); break;
                case 8 when text.Length > 0:
                    var at = random.Next(text.Length);
                    edit.EraseText(note, "body", at, Math.Min(1 + random.Next(2), text.Length - at));
                    break;
                case 9: edit.ReplaceText(note, "body", text.Length > 2 ? text[..^1] + "é" : text + "ü"); break;
                default: edit.InsertText(note, "body", random.Next(text.Length + 1), $"{(char)('a' + random.Next(26))}"); break;
            }
            if (edit.Count > 0)
            {
                try
                {
                    await edit.CommitAsync();
                }
                catch (InvalidOperationException)
                {
                    // An edit that does nothing (the same text again).
                }
            }
        }
        foreach (var device in devices)
            device.Network.Down = false;
        for (var round = 0; round < 2; round++)
            foreach (var device in devices)
                await device.SyncAsync();

        var connection = server.Connect(Users.Of("user"));
        var log = await connection.PullAsync(new PullRequest(collections, 0, 1000));
        var snapshot = await connection.SnapshotAsync(new SnapshotRequest(collections));
        var documents = snapshot.Documents.Select(DocumentState.FromSnapshot).ToList();
        var fixture = new JsonObject
        {
            ["comment"] = "Written by Routeables.Remote.Test (RRP_WRITE_FIXTURE=1). Merging \"log\" in order, or reading \"snapshot\", gives \"expected\".",
            ["protocol"] = RemoteProtocol.Version,
            ["log"] = JsonNode.Parse(JsonSerializer.Serialize(log, RemoteJsonContext.Default.PullResult)),
            ["snapshot"] = JsonNode.Parse(JsonSerializer.Serialize(snapshot, RemoteJsonContext.Default.SnapshotResult)),
            ["expected"] = Fingerprints(documents),
        };
        return fixture.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }
}
