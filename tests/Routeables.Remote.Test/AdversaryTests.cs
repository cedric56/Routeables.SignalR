using System.Security.Claims;

namespace Routeables.Remote.Tests;

/// <summary>What a client that does not follow the protocol can do to the others: nothing it was not allowed to.</summary>
public class AdversaryTests
{
    private static readonly DocumentId Page = new("Page", "home");
    private static readonly DateTimeOffset Noon = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private static Change Set(ReplicaId replica, long seq, Hlc stamp, string value)
        => new(new Dot(replica, seq), stamp, [new DocumentOp(Page, new RestoreDocument()), new DocumentOp(Page, new SetField("title", $"\"{value}\""))]);

    // ---------------------------------------------------------------- who owns a replica (B2, D6)

    [Fact]
    public async Task An_anonymous_user_cannot_write_in_the_name_of_another_one()
    {
        var server = new ReplicationServer(AllowAllReplicationPolicy.Instance);
        var victim = ReplicaId.New();
        var anonymous = server.Connect(null);

        // The replica of a victim is in the log for every reader: an anonymous user pushes its next change.
        var forged = await anonymous.PushAsync(new PushRequest([Set(victim, 1, new Hlc(1, 0, victim), "attacker")]));
        Assert.Equal(RemoteErrorCodes.Unidentified, Assert.Single(forged.Acks).Code);
        Assert.Empty(server.Log);

        // A user signed in without an identifier is not told apart either.
        var nameless = server.Connect(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "?")], "cookie")));
        Assert.Equal(RemoteErrorCodes.Unidentified, Assert.Single((await nameless.PushAsync(new PushRequest([Set(victim, 1, new Hlc(1, 0, victim), "attacker")]))).Acks).Code);

        // An anonymous user still says Hello, and reads.
        Assert.True((await anonymous.HelloAsync(new RemoteHello(RemoteProtocol.Version, ReplicaId.New(), default))).Success);
        Assert.False((await anonymous.PullAsync(new PullRequest(["Page"], 0))).SnapshotRequired);
    }

    [Fact]
    public async Task The_same_identifier_from_another_issuer_is_another_user()
    {
        var server = new ReplicationServer(AllowAllReplicationPolicy.Instance);
        var replica = ReplicaId.New();
        ClaimsPrincipal From(string issuer) => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "42", ClaimValueTypes.String, issuer)], "jwt"));

        Assert.Equal(AckStatus.Accepted, Assert.Single((await server.Connect(From("https://a.example")).PushAsync(new PushRequest([Set(replica, 1, new Hlc(1, 0, replica), "a")]))).Acks).Status);
        var other = await server.Connect(From("https://b.example")).PushAsync(new PushRequest([Set(replica, 2, new Hlc(2, 0, replica), "b")]));

        Assert.Equal(RemoteErrorCodes.ReplicaTaken, Assert.Single(other.Acks).Code);
    }

    // ---------------------------------------------------------------- the hybrid clock (B3)

    [Fact]
    public void A_clock_whose_counter_is_used_up_moves_to_the_next_millisecond()
    {
        var time = new ManualTime(Noon);
        var wall = Noon.ToUnixTimeMilliseconds();
        var replica = ReplicaId.New();

        var clock = new HybridClock(replica, time, new Hlc(wall, int.MaxValue, replica));
        var next = clock.Next();
        Assert.Equal((wall + 1, 0), (next.Wall, next.Counter));

        var observer = new HybridClock(ReplicaId.New(), time);
        observer.Observe(new Hlc(wall, int.MaxValue, replica));
        Assert.True(observer.Last > new Hlc(wall, int.MaxValue, replica));
        Assert.True(observer.Next() > observer.Last with { Counter = 0 });

        observer.Observe(new Hlc(wall + 10, -5, replica));   // never sent by a replica: not followed
        Assert.Equal(wall + 1, observer.Last.Wall);
    }

    [Fact]
    public async Task A_counter_at_its_limit_does_not_send_the_others_into_the_past()
    {
        var time = new ManualTime(Noon);
        var server = new ReplicationServer(AllowAllReplicationPolicy.Instance, time: time);
        var mallory = ReplicaId.New();
        var soon = Noon.AddMinutes(4).ToUnixTimeMilliseconds();   // within the skew allowed

        // A Hello alone, then a write, both at the end of the counter.
        var channel = server.Connect(Users.Of("mallory"));
        await channel.HelloAsync(new RemoteHello(RemoteProtocol.Version, mallory, new Hlc(soon, int.MaxValue, mallory)));
        Assert.True(server.Clock.Last.Counter >= 0);
        var evil = await channel.PushAsync(new PushRequest([Set(mallory, 1, new Hlc(soon, int.MaxValue, mallory), "evil")]));
        Assert.Equal(AckStatus.Accepted, Assert.Single(evil.Acks).Status);
        Assert.True(server.Clock.Last > new Hlc(soon, int.MaxValue, mallory));

        // Alice writes after it: her value wins.
        var alice = await Device.StartAsync("alice", server, user: "alice", collections: ["Page"], time: time);
        await alice.SyncAsync();
        await alice.Replica.Edit().Create(Page).Set(Page, "title", "\"alice\"").CommitAsync();
        await alice.SyncAsync();

        Assert.Equal("\"alice\"", server.Find(Page)!.Get("title"));
    }

    [Fact]
    public async Task A_stamp_with_a_negative_counter_is_refused()
    {
        var server = new ReplicationServer(AllowAllReplicationPolicy.Instance);
        var replica = ReplicaId.New();

        var result = await server.Connect(Users.Of("u")).PushAsync(new PushRequest([Set(replica, 1, new Hlc(1, int.MinValue, replica), "x")]));

        Assert.Equal(RemoteErrorCodes.Invalid, Assert.Single(result.Acks).Code);
    }

    // ---------------------------------------------------------------- pull and compaction (B4)

    /// <summary>A store that compacts its log while a pull reads it, as another request of the server would.</summary>
    private sealed class CompactingStore(MemoryReplicationStore inner) : IReplicationStore
    {
        public long? CompactOnRead { get; set; }

        public ValueTask<ReplicationHead> HeadAsync(CancellationToken ct = default) => inner.HeadAsync(ct);
        public ValueTask<ReplicaRecord?> FindReplicaAsync(ReplicaId replica, CancellationToken ct = default) => inner.FindReplicaAsync(replica, ct);
        public ValueTask<DocumentState?> FindAsync(DocumentId id, CancellationToken ct = default) => inner.FindAsync(id, ct);
        public ValueTask<IReadOnlyList<DocumentState>> DocumentsAsync(IReadOnlyCollection<string> collections, CancellationToken ct = default) => inner.DocumentsAsync(collections, ct);
        public ValueTask CommitAsync(ReplicationCommit commit, CancellationToken ct = default) => inner.CommitAsync(commit, ct);
        public ValueTask CompactAsync(long upTo, CancellationToken ct = default) => inner.CompactAsync(upTo, ct);

        public async ValueTask<IReadOnlyList<LogEntry>> ReadAsync(long after, long upTo, IReadOnlyCollection<string> collections, int max, CancellationToken ct = default)
        {
            if (CompactOnRead is { } position)
            {
                CompactOnRead = null;
                await inner.CompactAsync(position, ct);
            }
            return await inner.ReadAsync(after, upTo, collections, max, ct);
        }
    }

    [Fact]
    public async Task A_pull_during_a_compaction_asks_for_a_snapshot_rather_than_skip_entries()
    {
        var store = new CompactingStore(new MemoryReplicationStore());
        var server = new ReplicationServer(AllowAllReplicationPolicy.Instance, store: store);
        var replica = ReplicaId.New();
        var channel = server.Connect(Users.Of("u"));
        for (var seq = 1; seq <= 5; seq++)
            await channel.PushAsync(new PushRequest([Set(replica, seq, new Hlc(seq, 0, replica), "v" + seq)]));

        store.CompactOnRead = 3;
        var pulled = await channel.PullAsync(new PullRequest(["Page"], 0));

        Assert.True(pulled.SnapshotRequired);
        Assert.Empty(pulled.Entries);
        Assert.Equal(0, pulled.Position);
    }

    // ---------------------------------------------------------------- counters (B5)

    [Fact]
    public async Task A_counter_stays_where_every_implementation_adds_it_exactly()
    {
        var server = new ReplicationServer(AllowAllReplicationPolicy.Instance);
        var replica = ReplicaId.New();
        var channel = server.Connect(Users.Of("u"));
        Change Add(long seq, long delta) => new(new Dot(replica, seq), new Hlc(seq, 0, replica),
            [new DocumentOp(Page, new RestoreDocument()), new DocumentOp(Page, new Increment("n", delta))]);

        Assert.Equal(AckStatus.Accepted, Assert.Single((await channel.PushAsync(new PushRequest([Add(1, RemoteProtocol.MaxCounter)]))).Acks).Status);
        var beyond = Assert.Single((await channel.PushAsync(new PushRequest([Add(2, 1)]))).Acks);
        var huge = Assert.Single((await channel.PushAsync(new PushRequest([Add(3, long.MinValue)]))).Acks);
        Assert.Equal(AckStatus.Accepted, Assert.Single((await channel.PushAsync(new PushRequest([Add(4, -RemoteProtocol.MaxCounter)]))).Acks).Status);

        Assert.Equal(RemoteErrorCodes.TooLarge, beyond.Code);
        Assert.Equal(RemoteErrorCodes.TooLarge, huge.Code);
        Assert.Equal(0, server.Find(Page)!.Counter("n"));
        Assert.Equal(2, server.Log.Count);
    }

    // ---------------------------------------------------------------- what is deserialized (D10)

    [Fact]
    public async Task A_change_that_lacks_what_it_must_hold_is_refused_with_a_reason()
    {
        var server = new ReplicationServer(AllowAllReplicationPolicy.Instance);
        var replica = ReplicaId.New();
        var channel = server.Connect(Users.Of("u"));
        Change[] changes =
        [
            new(new Dot(replica, 1), new Hlc(1, 0, replica), null!),
            new(new Dot(replica, 2), new Hlc(2, 0, replica), [null!]),
            new(new Dot(replica, 3), new Hlc(3, 0, replica), [new DocumentOp(new DocumentId("Page", null!), new RestoreDocument())]),
            new(new Dot(replica, 4), new Hlc(4, 0, replica), [new DocumentOp(Page, new SetField(null!, "1"))]),
            new(new Dot(replica, 5), new Hlc(5, 0, replica), [new DocumentOp(Page, new InsertText("body", null, null!))]),
            new(new Dot(replica, 6), new Hlc(6, 0, replica), [new DocumentOp(Page, new RemoveElement("tags", "t", null!))]),
        ];

        var result = await channel.PushAsync(new PushRequest(changes));

        Assert.All(result.Acks, ack => Assert.Equal(RemoteErrorCodes.Invalid, ack.Code));
        await Assert.ThrowsAsync<ArgumentException>(() => channel.PushAsync(new PushRequest(null!)));
        await Assert.ThrowsAsync<ArgumentException>(() => channel.PullAsync(new PullRequest([null!], 0)));
    }

    // ---------------------------------------------------------------- texts (D3)

    [Fact]
    public async Task A_text_cannot_grow_beyond_its_limit()
    {
        var server = new ReplicationServer(AllowAllReplicationPolicy.Instance, new ReplicationServerOptions { MaxTextLength = 10 });
        var replica = ReplicaId.New();
        var channel = server.Connect(Users.Of("u"));
        Change Insert(long seq, string text) => new(new Dot(replica, seq), new Hlc(seq, 0, replica),
            [new DocumentOp(Page, new RestoreDocument()), new DocumentOp(Page, new InsertText("body", null, text))]);

        Assert.Equal(AckStatus.Accepted, Assert.Single((await channel.PushAsync(new PushRequest([Insert(1, "0123456789")]))).Acks).Status);
        Assert.Equal(RemoteErrorCodes.TooLarge, Assert.Single((await channel.PushAsync(new PushRequest([Insert(2, "!")]))).Acks).Code);
        Assert.Equal("0123456789", server.Find(Page)!.Text("body"));
    }

    [Fact]
    public void A_long_text_typed_and_erased_character_by_character_stays_fast_and_right()
    {
        var documents = new DocumentSet();
        var replica = ReplicaId.New();
        TextId? after = null;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var seq = 1; seq <= 20_000; seq++)
        {
            var ops = new List<DocumentOp> { new(Page, new InsertText("body", after, "x")) };
            if (seq == 1)
                ops.Insert(0, new DocumentOp(Page, new RestoreDocument()));
            documents.Apply(new Change(new Dot(replica, seq), new Hlc(seq, 0, replica), ops));
            after = new TextId(new Dot(replica, seq), seq == 1 ? 1 : 0, 0);
        }
        var spans = Enumerable.Range(2, 9_999).Select(seq => new TextSpan(new Dot(replica, seq), 0, 0, 1)).ToList();
        documents.Apply(new Change(new Dot(replica, 20_001), new Hlc(20_001, 0, replica), [new DocumentOp(Page, new EraseText("body", spans))]));

        Assert.Equal(20_000 - 9_999, documents.Find(Page)!.Text("body").Length);
        Assert.Equal(20_000, documents.Find(Page)!.TextSize("body"));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"{watch.Elapsed}");
    }

    // ---------------------------------------------------------------- snapshots and writes (D2)

    /// <summary>A policy whose decisions wait until the test lets them go.</summary>
    private sealed class SlowPolicy : IReplicationPolicy
    {
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Deciding { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<Admission> AdmitAsync(ClaimsPrincipal? user, Change change, Func<DocumentId, ValueTask<DocumentState?>> current, CancellationToken ct)
        {
            Deciding.TrySetResult();
            await Gate.Task;
            return Admission.Accept;
        }

        public bool CanRead(ClaimsPrincipal? user, DocumentId document, DocumentState? state) => true;

        public bool CanFollow(ClaimsPrincipal? user, string collection) => collection != "Secret";
    }

    [Fact]
    public async Task A_snapshot_does_not_wait_for_a_decision_and_holds_the_log_up_to_its_position()
    {
        var policy = new SlowPolicy();
        policy.Gate.SetResult();
        var server = new ReplicationServer(policy);
        var replica = ReplicaId.New();
        var channel = server.Connect(Users.Of("u"));
        await channel.PushAsync(new PushRequest([Set(replica, 1, new Hlc(1, 0, replica), "one")]));

        var slow = new SlowPolicy();
        var blocked = new ReplicationServer(slow, store: server.Store);
        var pushing = blocked.Connect(Users.Of("u")).PushAsync(new PushRequest([Set(replica, 2, new Hlc(2, 0, replica), "two")]));
        await slow.Deciding.Task;

        var snapshot = await blocked.Connect(Users.Of("v")).SnapshotAsync(new SnapshotRequest(["Page"]))
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);   // the decision still waits
        Assert.Equal(1, snapshot.Position);
        Assert.Equal("\"one\"", DocumentState.FromSnapshot(Assert.Single(snapshot.Documents)).Get("title"));

        slow.Gate.SetResult();
        Assert.Equal(AckStatus.Accepted, Assert.Single((await pushing).Acks).Status);
        Assert.Equal(2, (await blocked.Connect(Users.Of("v")).SnapshotAsync(new SnapshotRequest(["Page"]))).Position);
    }

    // ---------------------------------------------------------------- following a collection (D5)

    [Fact]
    public void The_rules_decide_who_is_told_that_a_collection_moves()
    {
        Assert.True(new ReplicationServer(AllowAllReplicationPolicy.Instance).CanFollow(Users.Of("u"), "Secret"));
        var server = new ReplicationServer(new SlowPolicy());
        Assert.False(server.CanFollow(Users.Of("u"), "Secret"));
        Assert.True(server.CanFollow(Users.Of("u"), "Page"));
    }
}
