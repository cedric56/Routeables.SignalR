using System.Security.Claims;

namespace Routeables.Remote.Tests;

/// <summary>The decisions of the server for several replicas at once (ConcurrentAdmission).</summary>
public class ConcurrencyTests
{
    private static readonly DocumentId Shared = new("Page", "shared");
    private static readonly DocumentId Other = new("Page", "other");

    private static Change Set(ReplicaId replica, long seq, DocumentId document, string value)
        => new(new Dot(replica, seq), new Hlc(seq, 0, replica), [new DocumentOp(document, new RestoreDocument()), new DocumentOp(document, new SetField("title", $"\"{value}\""))]);

    /// <summary>Holds the decisions of the user "slow" until it is released; reads the document <see cref="Shared"/> each time.</summary>
    private sealed class GatePolicy : IReplicationPolicy
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<bool> SlowSawShared { get; } = [];

        public async ValueTask<Admission> AdmitAsync(ClaimsPrincipal? user, Change change, Func<DocumentId, ValueTask<DocumentState?>> current, CancellationToken ct)
        {
            if (user?.FindFirst(ClaimTypes.NameIdentifier)?.Value != "slow")
                return Admission.Accept;
            var shared = await current(Shared);
            lock (SlowSawShared)
                SlowSawShared.Add(shared is not null);
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return Admission.Accept;
        }

        public bool CanRead(ClaimsPrincipal? user, DocumentId document, DocumentState? state) => true;
    }

    [Fact]
    public async Task A_replica_whose_decision_waits_does_not_hold_the_others()
    {
        var policy = new GatePolicy();
        var server = new ReplicationServer(policy);
        var slow = ReplicaId.New();
        var fast = ReplicaId.New();

        var waiting = server.Connect(Users.Of("slow")).PushAsync(new PushRequest([Set(slow, 1, Other, "slow")]), TestContext.Current.CancellationToken);
        await policy.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Before, one decision at a time for the whole server: this push waited for the one above.
        var quick = await server.Connect(Users.Of("fast")).PushAsync(new PushRequest([Set(fast, 1, Other, "fast")]), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1L, Assert.Single(quick.Acks).Position);
        Assert.False(waiting.IsCompleted);

        policy.Release.SetResult();
        var late = await waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(2L, Assert.Single(late.Acks).Position);   // the log stays in the order of the writes
        Assert.Equal([1L, 2L], server.Log.Select(e => e.Position));
    }

    [Fact]
    public async Task A_decision_whose_documents_were_written_meanwhile_is_made_again_on_what_is_stored_now()
    {
        var policy = new GatePolicy();
        var server = new ReplicationServer(policy);
        var slow = ReplicaId.New();
        var fast = ReplicaId.New();

        // The rule of "slow" read Shared while it did not exist yet; "fast" creates it before "slow" is written.
        var waiting = server.Connect(Users.Of("slow")).PushAsync(new PushRequest([Set(slow, 1, Other, "slow")]), TestContext.Current.CancellationToken);
        await policy.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await server.Connect(Users.Of("fast")).PushAsync(new PushRequest([Set(fast, 1, Shared, "fast")]), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        policy.Release.SetResult();
        var late = await waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(AckStatus.Accepted, Assert.Single(late.Acks).Status);
        Assert.Equal([false, true], policy.SlowSawShared);   // decided again, and the rule saw the document as it is now
        Assert.Equal(1, server.Log.Count(e => e.Change.Dot.Replica == slow));
    }

    [Fact]
    public async Task Decisions_are_made_one_at_a_time_when_the_options_say_so()
    {
        var policy = new GatePolicy();
        var server = new ReplicationServer(policy, new ReplicationServerOptions { ConcurrentAdmission = false });
        var slow = ReplicaId.New();
        var fast = ReplicaId.New();

        var waiting = server.Connect(Users.Of("slow")).PushAsync(new PushRequest([Set(slow, 1, Other, "slow")]), TestContext.Current.CancellationToken);
        await policy.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var quick = server.Connect(Users.Of("fast")).PushAsync(new PushRequest([Set(fast, 1, Other, "fast")]), TestContext.Current.CancellationToken);

        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.False(quick.IsCompleted);

        policy.Release.SetResult();
        Assert.Equal(1L, Assert.Single((await waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).Acks).Position);
        Assert.Equal(2L, Assert.Single((await quick.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).Acks).Position);
    }

    [Fact]
    public async Task Many_replicas_pushing_at_once_get_every_position_once()
    {
        var server = new ReplicationServer(AllowAllReplicationPolicy.Instance);
        var pushes = Enumerable.Range(0, 32).Select(i => Task.Run(async () =>
        {
            var replica = ReplicaId.New();
            var channel = server.Connect(Users.Of("u" + i));
            var positions = new List<long>();
            for (var seq = 1; seq <= 10; seq++)
            {
                var document = new DocumentId("Page", (seq % 3).ToString(System.Globalization.CultureInfo.InvariantCulture));
                positions.Add(Assert.Single((await channel.PushAsync(new PushRequest([Set(replica, seq, document, $"{i}-{seq}")]))).Acks).Position);
            }
            return positions;
        })).ToList();

        var all = (await Task.WhenAll(pushes)).SelectMany(p => p).OrderBy(p => p).ToList();
        Assert.Equal(Enumerable.Range(1, 320).Select(p => (long)p), all);
        Assert.Equal(Enumerable.Range(1, 320).Select(p => (long)p), server.Log.Select(e => e.Position));
    }
}
