using System.Security.Claims;
using System.Text.Json;

namespace Routeables.Remote.Tests;

/// <summary>What a network, a server and a process do to a replica, and what the protocol guarantees anyway.</summary>
public class ResilienceTests
{
    private static readonly DocumentId Ana = new("Person", "ana");

    [Fact]
    public async Task A_push_whose_answer_was_lost_is_not_applied_twice()
    {
        var server = new ReplicationServer();
        var a = await Device.StartAsync("A", server);
        await a.Replica.Edit().Create(Ana).Increment(Ana, "visits", 1).CommitAsync();

        a.Network.LoseNextPushAnswer = true;
        Assert.False(await a.TrySyncAsync());
        Assert.Equal(1, server.Find(Ana)!.Counter("visits"));      // applied, the client does not know

        var report = await a.SyncAsync();
        Assert.Equal(1, server.Find(Ana)!.Counter("visits"));      // sent again, recognized
        Assert.Equal(1, a.Replica.Find(Ana)!.Counter("visits"));
        Assert.Equal(0, a.Replica.PendingCount);
        Assert.Single(server.Log);
        Assert.Equal(0, report.Rejected);
    }

    [Fact]
    public async Task A_change_received_twice_is_applied_once()
    {
        var documents = new DocumentSet();
        var change = new Change(new Dot(ReplicaId.New(), 1), new Hlc(1, 0, ReplicaId.New()),
            [new DocumentOp(Ana, new RestoreDocument()), new DocumentOp(Ana, new Increment("visits", 2))]);

        Assert.True(documents.Apply(change));
        Assert.False(documents.Apply(change));
        Assert.Equal(2, documents.Find(Ana)!.Counter("visits"));
    }

    [Fact]
    public async Task A_restart_keeps_the_offline_changes_and_the_cursor()
    {
        var server = new ReplicationServer();
        var a = await Device.StartAsync("A", server);
        var b = await Device.StartAsync("B", server);
        await b.Replica.Edit().Create(Ana).Set(Ana, "name", "\"Ana\"").CommitAsync();
        await b.SyncAsync();
        await a.SyncAsync();
        var cursor = a.Replica.Cursor;

        a.Network.Down = true;
        await a.Replica.Edit().Set(Ana, "city", "\"Lyon\"").CommitAsync();
        var id = a.Replica.Id;
        await a.RestartAsync();

        Assert.Equal(id, a.Replica.Id);
        Assert.Equal(cursor, a.Replica.Cursor);
        Assert.Equal(1, a.Replica.PendingCount);
        Assert.Equal("\"Lyon\"", a.Replica.Find(Ana)!.Get("city"));

        a.Network.Down = false;
        await a.SyncAsync();
        await b.SyncAsync();
        Assert.Equal("\"Lyon\"", b.Replica.Find(Ana)!.Get("city"));
    }

    [Fact]
    public async Task A_stop_between_the_push_and_the_pull_loses_nothing_and_duplicates_nothing()
    {
        var server = new ReplicationServer();
        var a = await Device.StartAsync("A", server);
        await a.Replica.Edit().Create(Ana).Increment(Ana, "visits", 1).CommitAsync();

        a.Network.LoseNextPullAnswer = true;             // pushed and accepted, then the pull fails
        Assert.False(await a.TrySyncAsync());
        await a.RestartAsync();
        Assert.Equal(1, a.Replica.Find(Ana)!.Counter("visits"));    // accepted, still shown once

        await a.SyncAsync();
        Assert.Equal(1, a.Replica.Find(Ana)!.Counter("visits"));
        Assert.Equal(1, server.Find(Ana)!.Counter("visits"));
    }

    [Fact]
    public async Task A_server_restarted_over_its_store_goes_on_where_it_stopped()
    {
        var store = new MemoryReplicationStore();
        var server = new ReplicationServer(store: store);
        var a = await Device.StartAsync("A", server);
        var b = await Device.StartAsync("B", server);
        await a.Replica.Edit().Create(Ana).Increment(Ana, "visits", 1).CommitAsync();
        a.Network.LoseNextPushAnswer = true;               // decided, the answer lost; then the server stops
        Assert.False(await a.TrySyncAsync());

        var restarted = new ReplicationServer(store: store);
        a.ConnectTo(restarted);
        b.ConnectTo(restarted);
        await b.Replica.Edit().Increment(Ana, "visits", 2).CommitAsync();
        await a.SyncAsync();                                 // its push is recognized by the new process
        await b.SyncAsync();
        await a.SyncAsync();

        Assert.Equal(3, restarted.Find(Ana)!.Counter("visits"));
        Assert.Equal(3, a.Replica.Find(Ana)!.Counter("visits"));
        Assert.Equal(3, b.Replica.Find(Ana)!.Counter("visits"));
        Assert.Equal([1L, 2L], restarted.Log.Select(e => e.Position));
    }

    [Fact]
    public async Task A_replica_far_behind_a_compacted_log_gets_a_snapshot_and_counts_its_own_changes_once()
    {
        var server = new ReplicationServer();
        var a = await Device.StartAsync("A", server);
        var b = await Device.StartAsync("B", server);
        await b.Replica.Edit().Create(Ana).Increment(Ana, "visits", 10).CommitAsync();
        await b.SyncAsync();

        // A pushes, then the pull fails: its change is accepted, still pending; the log is compacted meanwhile.
        await a.Replica.Edit().Increment(Ana, "visits", 1).CommitAsync();
        a.Network.LoseNextPullAnswer = true;
        Assert.False(await a.TrySyncAsync());
        for (var i = 0; i < 5; i++)
        {
            await b.Replica.Edit().Increment(Ana, "visits", 1).CommitAsync();
            await b.SyncAsync();
        }
        await server.CompactAsync(server.Position);

        var report = await a.SyncAsync();
        Assert.True(report.Snapshot);
        Assert.Equal(16, server.Find(Ana)!.Counter("visits"));
        Assert.Equal(16, a.Replica.Find(Ana)!.Counter("visits"));   // its own +1 is in the snapshot, not added again
        Assert.Equal(0, a.Replica.PendingCount);
    }

    [Fact]
    public async Task A_clock_in_the_future_is_corrected_and_the_change_sent_again()
    {
        var serverTime = new ManualTime(DateTimeOffset.UnixEpoch.AddDays(20_000));
        var deviceTime = new ManualTime(serverTime.Now.AddDays(3));     // a device three days ahead
        var server = new ReplicationServer(time: serverTime);
        var a = await Device.StartAsync("A", server, time: deviceTime);
        await a.Replica.Edit().Create(Ana).Set(Ana, "name", "\"Ana\"").CommitAsync();

        deviceTime.Now = serverTime.Now;           // the clock of the device was fixed meanwhile
        await a.SyncAsync();

        Assert.Equal("\"Ana\"", server.Find(Ana)!.Get("name"));
        Assert.Empty(a.Replica.Rejected);
    }

    [Fact]
    public async Task A_removal_of_an_element_added_by_a_change_stamped_again_still_removes_it()
    {
        var serverTime = new ManualTime(DateTimeOffset.UnixEpoch.AddDays(20_000));
        var deviceTime = new ManualTime(serverTime.Now.AddDays(3));
        var server = new ReplicationServer(time: serverTime);
        var a = await Device.StartAsync("A", server, time: deviceTime);
        await a.Replica.Edit().Create(Ana).AddElement(Ana, "tags", "x").CommitAsync();
        await a.Replica.Edit().RemoveElement(Ana, "tags", "x").CommitAsync();

        deviceTime.Now = serverTime.Now;
        await a.SyncAsync();

        Assert.Empty(server.Find(Ana)!.Elements("tags"));
        Assert.Empty(a.Replica.Find(Ana)!.Elements("tags"));
    }

    [Fact]
    public async Task A_clock_still_wrong_keeps_the_change_pending_and_does_not_carry_the_server_into_its_future()
    {
        var serverTime = new ManualTime(DateTimeOffset.UnixEpoch.AddDays(20_000));
        var deviceTime = new ManualTime(serverTime.Now.AddDays(3));
        var server = new ReplicationServer(time: serverTime);
        var a = await Device.StartAsync("A", server, time: deviceTime);
        await a.Replica.Edit().Create(Ana).Set(Ana, "name", "\"Ana\"").CommitAsync();

        await a.SyncAsync();                         // returns: no endless loop

        Assert.Equal(1, a.Replica.PendingCount);     // the intent is kept for the next synchronization
        Assert.Empty(a.Replica.Rejected);
        Assert.Null(server.Find(Ana));
        Assert.True(server.Clock.Last.Wall < deviceTime.Now.ToUnixTimeMilliseconds() - 1000);
    }

    [Fact]
    public async Task The_messages_travel_as_json_without_reflection()
    {
        var change = new Change(new Dot(ReplicaId.New(), 3), new Hlc(42, 1, ReplicaId.New()),
        [
            new DocumentOp(Ana, new RestoreDocument()),
            new DocumentOp(Ana, new SetField("name", "\"Ana\"")),
            new DocumentOp(Ana, new AddElement("tags", "\"a\"")),
            new DocumentOp(Ana, new RemoveElement("tags", "\"b\"", [new ElementTag(new Dot(ReplicaId.New(), 1), 0)])),
            new DocumentOp(Ana, new Increment("visits", -2)),
            new DocumentOp(Ana, new DeleteDocument()),
        ]);

        var json = JsonSerializer.Serialize(new PushRequest([change]), RemoteJsonContext.Default.PushRequest);
        var back = JsonSerializer.Deserialize(json, RemoteJsonContext.Default.PushRequest)!;

        Assert.Contains("\"op\":\"remove\"", json);
        Assert.Equal(change.Dot, back.Changes[0].Dot);
        Assert.Equal(change.Ops.Select(o => o.Op.GetType()), back.Changes[0].Ops.Select(o => o.Op.GetType()));
        Assert.Equal(-2, ((Increment)back.Changes[0].Ops[4].Op).Delta);
    }

    [Fact]
    public async Task The_stored_state_survives_every_kind_of_operation()
    {
        var storage = new MemoryReplicaStorage();
        var replica = await Replica.OpenAsync(storage, ["Person"]);
        await replica.Edit().Create(Ana).Set(Ana, "name", null).AddElement(Ana, "tags", "x").Increment(Ana, "n", 3).CommitAsync();
        await replica.Edit().RemoveElement(Ana, "tags", "x").Delete(Ana).CommitAsync();

        var reopened = await Replica.OpenAsync(storage, ["Person"]);

        Assert.Equal(replica.Id, reopened.Id);
        Assert.Equal(2, reopened.PendingCount);
        var state = reopened.Find(Ana)!;
        Assert.False(state.Exists);
        Assert.Empty(state.Elements("tags"));
        Assert.Equal(3, state.Counter("n"));
        Assert.True(reopened.Clock.Last >= replica.Clock.Last);
    }
}

/// <summary>What the server decides, and what each user may read.</summary>
public class AdmissionTests
{
    private static readonly DocumentId Ana = new("Person", "ana");

    /// <summary>A document belongs to the user named in its "owner" field; a name "forbidden" is refused.</summary>
    private sealed class OwnerPolicy : IReplicationPolicy
    {
        private static string? UserOf(ClaimsPrincipal? user) => user?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        public async ValueTask<Admission> AdmitAsync(ClaimsPrincipal? user, Change change, Func<DocumentId, ValueTask<DocumentState?>> current, CancellationToken ct)
        {
            foreach (var op in change.Ops)
            {
                if (op.Op is SetField { Field: "name", Value: "\"forbidden\"" })
                    return Admission.Reject(RemoteErrorCodes.Invalid, "This name is not allowed.");
                // A document of someone else, as stored now, cannot be changed; nor can its owner be changed to someone else.
                var owner = (await current(op.Document))?.Get("owner");
                if (owner is not null && owner != $"\"{UserOf(user)}\"")
                    return Admission.Reject(RemoteErrorCodes.Forbidden, "This document belongs to another user.");
                if (op.Op is SetField { Field: "owner" } set && set.Value != $"\"{UserOf(user)}\"")
                    return Admission.Reject(RemoteErrorCodes.Forbidden, "A document cannot be given away.");
            }
            return Admission.Accept;
        }

        public bool CanRead(ClaimsPrincipal? user, DocumentId document, DocumentState? state)
            => state?.Get("owner") is not { } owner || owner == $"\"{UserOf(user)}\"";
    }

    [Fact]
    public async Task A_refused_change_disappears_exactly_and_is_kept_for_the_application()
    {
        var server = new ReplicationServer(new OwnerPolicy());
        var ana = await Device.StartAsync("A", server, user: "ana");
        await ana.Replica.Edit().Create(Ana).Set(Ana, "owner", "\"ana\"").Set(Ana, "name", "\"Ana\"").CommitAsync();
        await ana.SyncAsync();

        await ana.Replica.Edit().Set(Ana, "name", "\"forbidden\"").Increment(Ana, "visits", 1).CommitAsync();
        Assert.Equal("\"forbidden\"", ana.Replica.Find(Ana)!.Get("name"));       // offline, at once

        var report = await ana.SyncAsync();

        Assert.Equal(1, report.Rejected);
        Assert.Equal("\"Ana\"", ana.Replica.Find(Ana)!.Get("name"));             // the whole change is gone: name and counter
        Assert.Equal(0, ana.Replica.Find(Ana)!.Counter("visits"));
        var rejected = Assert.Single(ana.Replica.Rejected);
        Assert.Equal(RemoteErrorCodes.Invalid, rejected.Code);
        Assert.Equal(2, rejected.Change.Ops.Count);
        Assert.Equal("\"Ana\"", server.Find(Ana)!.Get("name"));
    }

    [Fact]
    public async Task A_change_is_admitted_whole_or_not_at_all()
    {
        var server = new ReplicationServer(new OwnerPolicy());
        var ana = await Device.StartAsync("A", server, user: "ana");
        var child = new DocumentId("Contact", "c1");

        await ana.Replica.Edit()
            .Create(Ana).Set(Ana, "owner", "\"ana\"").Set(Ana, "name", "\"forbidden\"")
            .Create(child).Set(child, "owner", "\"ana\"")
            .CommitAsync();
        await ana.SyncAsync();

        Assert.Null(server.Find(Ana));
        Assert.Null(server.Find(child));
        Assert.Empty(server.Log);
    }

    [Fact]
    public async Task A_user_never_receives_the_documents_of_another_and_its_cursor_still_moves()
    {
        var server = new ReplicationServer(new OwnerPolicy());
        var ana = await Device.StartAsync("A", server, user: "ana");
        var bob = await Device.StartAsync("B", server, user: "bob");
        var bobs = new DocumentId("Person", "bob");

        await ana.Replica.Edit().Create(Ana).Set(Ana, "owner", "\"ana\"").Set(Ana, "secret", "\"s\"").CommitAsync();
        await bob.Replica.Edit().Create(bobs).Set(bobs, "owner", "\"bob\"").CommitAsync();
        await ana.SyncAsync();
        await bob.SyncAsync();

        Assert.Null(bob.Replica.Find(Ana));
        Assert.Equal(server.Position, bob.Replica.Cursor);
        Assert.Single(bob.Replica.All("Person"));
        var pulled = await server.Connect(Users.Of("bob")).PullAsync(new PullRequest(["Person"], 0));
        Assert.DoesNotContain(pulled.Entries.SelectMany(e => e.Change.Ops), o => o.Document == Ana);

        // A snapshot follows the same rule.
        var snapshot = await server.Connect(Users.Of("bob")).SnapshotAsync(new SnapshotRequest(["Person"]));
        Assert.DoesNotContain(snapshot.Documents, d => d.Id == Ana);
        Assert.Contains(snapshot.Documents, d => d.Id == bobs);
    }

    [Fact]
    public async Task A_user_cannot_change_a_document_of_another_even_offline()
    {
        var server = new ReplicationServer(new OwnerPolicy());
        var ana = await Device.StartAsync("A", server, user: "ana");
        var mallory = await Device.StartAsync("M", server, user: "mallory");
        await ana.Replica.Edit().Create(Ana).Set(Ana, "owner", "\"ana\"").Set(Ana, "name", "\"Ana\"").CommitAsync();
        await ana.SyncAsync();

        // Mallory guesses the key and writes offline as if the document were new.
        await mallory.Replica.Edit().Create(Ana).Set(Ana, "name", "\"pwned\"").CommitAsync();
        var report = await mallory.SyncAsync();

        Assert.Equal(1, report.Rejected);
        Assert.Equal("\"Ana\"", server.Find(Ana)!.Get("name"));
    }

    [Fact]
    public async Task A_replica_cannot_be_used_by_another_user()
    {
        var server = new ReplicationServer();
        var ana = await Device.StartAsync("A", server, user: "ana");
        await ana.Replica.Edit().Create(Ana).CommitAsync();
        await ana.SyncAsync();

        var stolen = server.Connect(Users.Of("mallory"));
        var welcome = await stolen.HelloAsync(new RemoteHello(RemoteProtocol.Version, ana.Replica.Id, ana.Replica.Clock.Last));
        Assert.False(welcome.Success);

        var forged = new Change(new Dot(ana.Replica.Id, 2), ana.Replica.Clock.Next(), [new DocumentOp(Ana, new DeleteDocument())]);
        var result = await stolen.PushAsync(new PushRequest([forged]));
        Assert.Equal(RemoteErrorCodes.ReplicaTaken, Assert.Single(result.Acks).Code);
        Assert.True(server.Find(Ana)!.Exists);
    }

    [Fact]
    public async Task A_client_of_another_version_is_refused_with_a_reason()
    {
        var welcome = await new ReplicationServer().Connect(null)
            .HelloAsync(new RemoteHello(RemoteProtocol.Version + 1, ReplicaId.New(), default));
        Assert.False(welcome.Success);
        Assert.Contains("update the client", welcome.Error);
    }
}
