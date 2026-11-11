using System.Collections;
using Routeables.Abstraction.Registrations;
using Routeables.SignalR;

namespace Routeables.Tests.Remote;

/// <summary>
/// The merge of the patches of one target (one turn of the dispatcher, or the changes that wait for a slow client): the client must
/// end with the state it would have had with every patch, never with another one.
/// </summary>
public sealed class PatchMergeTests
{
    // ---------------------------------------------------------------- what the client does with patches

    private static object? Clone(object? value) => value switch
    {
        null or string => value,
        IDictionary<string, object?> map => map.ToDictionary(e => e.Key, e => Clone(e.Value), StringComparer.Ordinal),
        IReadOnlyDictionary<string, object?> map => map.ToDictionary(e => e.Key, e => Clone(e.Value), StringComparer.Ordinal),
        IEnumerable items => items.Cast<object?>().Select(Clone).ToList(),
        _ => value,
    };

    private static object? Child(object container, object segment) => container switch
    {
        Dictionary<string, object?> map => map[(string)segment],
        List<object?> list => list[Convert.ToInt32(segment, System.Globalization.CultureInfo.InvariantCulture)],
        _ => throw new InvalidOperationException("Not a container."),
    };

    /// <summary>Applies a patch as @routeables/client does.</summary>
    private static void Apply(Dictionary<string, object?> state, RouteablePatch patch)
    {
        object container = state;
        for (var i = 0; i < patch.Path.Count - (patch.Op == RouteablePatch.Set ? 1 : 0); i++)
            container = Child(container, patch.Path[i])!;
        if (patch.Op == RouteablePatch.Splice)
        {
            var list = (List<object?>)container;
            list.RemoveRange(patch.Index!.Value, patch.Remove!.Value);
            list.InsertRange(patch.Index.Value, (List<object?>)Clone(patch.Value)!);
            return;
        }
        var last = patch.Path[^1];
        if (container is Dictionary<string, object?> map)
            map[(string)last] = Clone(patch.Value);
        else
            ((List<object?>)container)[Convert.ToInt32(last, System.Globalization.CultureInfo.InvariantCulture)] = Clone(patch.Value);
    }

    private static Dictionary<string, object?> Run(Dictionary<string, object?> start, IEnumerable<RouteablePatch> patches)
    {
        var state = (Dictionary<string, object?>)Clone(start)!;
        foreach (var patch in patches)
            Apply(state, patch);
        return state;
    }

    private static List<string?> Names(Dictionary<string, object?> state)
        => [.. ((List<object?>)state["items"]!).Select(i => (string?)((Dictionary<string, object?>)i!)["name"])];

    private static Dictionary<string, object?> Item(string name) => new(StringComparer.Ordinal) { ["name"] = name };

    private static RouteablePatch Rename(int index, string name) => new(RouteablePatch.Set, ["items", index, "name"], name);

    // ---------------------------------------------------------------- the merge alone

    [Fact]
    public void A_set_after_a_splice_keeps_the_sets_that_named_other_items()
    {
        var start = new Dictionary<string, object?>(StringComparer.Ordinal) { ["items"] = new List<object?> { Item("A"), Item("B"), Item("C"), Item("D") } };
        RouteablePatch[] said = [Rename(2, "C'"), new(RouteablePatch.Splice, ["items"], new List<object?>(), 0, 1), Rename(2, "D'")];

        var merged = new List<RouteablePatch>();
        foreach (var patch in said)
            RouteablePatches.Add(merged, patch);

        Assert.Equal(["B", "C'", "D'"], Names(Run(start, said)));
        Assert.Equal(["B", "C'", "D'"], Names(Run(start, merged)));
    }

    [Fact]
    public void A_set_still_replaces_what_was_said_of_the_same_value_since_the_last_splice()
    {
        var merged = new List<RouteablePatch>();
        RouteablePatches.Add(merged, Rename(1, "x"));
        RouteablePatches.Add(merged, new(RouteablePatch.Splice, ["items"], new List<object?> { Item("new") }, 0, 0));
        RouteablePatches.Add(merged, Rename(1, "y"));
        RouteablePatches.Add(merged, Rename(1, "z"));
        RouteablePatches.Add(merged, new(RouteablePatch.Set, ["title"], "t"));

        Assert.Equal(4, merged.Count);   // x (before the splice), the splice, z, the title
        Assert.Equal("z", merged[2].Value);
    }

    /// <summary>Random sets and splices, merged or not: the same state at the end.</summary>
    [Fact]
    public void Random_sets_and_splices_end_the_same_merged_or_not()
    {
        for (var seed = 0; seed < 2000; seed++)
        {
            var random = new Random(seed);
            var start = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["title"] = "t",
                ["items"] = Enumerable.Range(0, random.Next(1, 6)).Select(i => (object?)Item("i" + i)).ToList(),
            };
            var shadow = (Dictionary<string, object?>)Clone(start)!;
            var said = new List<RouteablePatch>();
            for (var step = 0; step < 12; step++)
            {
                var count = ((List<object?>)shadow["items"]!).Count;
                RouteablePatch patch = random.Next(5) switch
                {
                    0 => new(RouteablePatch.Set, ["title"], "t" + step),
                    1 when count > 0 => new(RouteablePatch.Set, ["items", random.Next(count)], Item("s" + step)),
                    2 => new(RouteablePatch.Splice, ["items"], new List<object?> { Item("n" + step) }, random.Next(count + 1), 0),
                    3 when count > 0 => new(RouteablePatch.Splice, ["items"], new List<object?>(), random.Next(count), 1),
                    4 when step % 5 == 4 => new(RouteablePatch.Set, ["items"], Enumerable.Range(0, count).Select(i => (object?)Item("w" + i)).ToList()),
                    _ when count > 0 => Rename(random.Next(count), "r" + step),
                    _ => new(RouteablePatch.Set, ["title"], "u" + step),
                };
                Apply(shadow, patch);
                said.Add(patch);
            }

            var merged = new List<RouteablePatch>();
            foreach (var patch in said)
                RouteablePatches.Add(merged, patch);

            var expected = Run(start, said);
            var actual = Run(start, merged);
            Assert.True(Names(expected).SequenceEqual(Names(actual)) && Equals(expected["title"], actual["title"]),
                $"seed {seed}: [{string.Join(", ", Names(expected))}] expected, [{string.Join(", ", Names(actual))}] after the merge");
        }
    }

    // ---------------------------------------------------------------- in a session

    private static ServiceProvider Services()
    {
        var services = TestSchema.Services();
        services.RegisterRoute<StreamView, StreamViewModel>(route: "/stream");
        return services.BuildServiceProvider();
    }

    private static List<string?> Replay(RecordingClient client)
    {
        var page = client.Navigated.Last(n => n.Target == RouteableTargets.Page);
        var state = (Dictionary<string, object?>)Clone(page.State)!;
        foreach (var batch in client.Batches.Where(b => b.Target == RouteableTargets.Page && b.Generation == page.Generation))
            foreach (var patch in batch.Patches)
                Apply(state, patch);
        return [.. ((List<object?>)state["lines"]!).Select(l => (string?)((Dictionary<string, object?>)l!)["text"])];
    }

    [Fact]
    public async Task An_item_renamed_then_moved_by_a_removal_in_one_turn_reaches_the_client()
    {
        await using var provider = Services();
        var client = new RecordingClient();
        await using var session = new RouteableSession(provider, client);
        await session.GoToAsync("/stream", TestContext.Current.CancellationToken);
        var viewModel = (StreamViewModel)session.Current!;
        await session.Dispatcher.InvokeAsync(() => { foreach (var text in "ABCD") viewModel.Lines.Add(new Line(text.ToString())); });

        await session.Dispatcher.InvokeAsync(() =>
        {
            viewModel.Lines[2].Text = "C'";
            viewModel.Lines.RemoveAt(0);
            viewModel.Lines[2].Text = "D'";
        });
        await session.FlushAsync();

        Assert.Equal(["B", "C'", "D'"], Replay(client));
    }

    [Fact]
    public async Task The_changes_merged_for_a_slow_client_give_it_the_state_of_the_server()
    {
        await using var provider = Services();
        var slow = new SlowClient(new RecordingClient());
        await using var session = new RouteableSession(provider, slow);
        await session.GoToAsync("/stream", TestContext.Current.CancellationToken);
        var viewModel = (StreamViewModel)session.Current!;
        await session.Dispatcher.InvokeAsync(() => { foreach (var text in "ABCD") viewModel.Lines.Add(new Line(text.ToString())); });
        await session.FlushAsync();

        slow.Close();
        await session.Dispatcher.InvokeAsync(() => viewModel.Value = 1);   // sent first: the next ones wait behind it, merged
        await session.Dispatcher.InvokeAsync(() => viewModel.Lines[2].Text = "C'");
        await session.Dispatcher.InvokeAsync(() => viewModel.Lines.RemoveAt(0));
        await session.Dispatcher.InvokeAsync(() => viewModel.Lines[2].Text = "D'");
        slow.Release();
        await session.FlushAsync();

        Assert.Equal(["B", "C'", "D'"], Replay(slow.Inner));
    }
}
