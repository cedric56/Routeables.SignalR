namespace Routeables.Remote.Tests;

/// <summary>Text fields: two people write in one field, offline, and both keep what they wrote.</summary>
public class TextTests
{
    private static readonly DocumentId Note = new("Note", "n1");

    private static async Task<(ReplicationServer Server, Device A, Device B, ManualTime Time)> StartAsync(string text)
    {
        var time = new ManualTime(DateTimeOffset.UnixEpoch.AddDays(20_000));
        var server = new ReplicationServer(AllowAllReplicationPolicy.Instance, time: time);
        var a = await Device.StartAsync("A", server, collections: ["Note"], time: time);
        var b = await Device.StartAsync("B", server, collections: ["Note"], time: time);
        await a.Replica.Edit().Create(Note).InsertText(Note, "body", 0, text).CommitAsync();
        await a.SyncAsync();
        await b.SyncAsync();
        return (server, a, b, time);
    }

    private static async Task SyncAllAsync(params Device[] devices)
    {
        for (var round = 0; round < 2; round++)
            foreach (var device in devices)
                await device.SyncAsync();
    }

    [Fact]
    public async Task Edits_at_two_places_of_one_text_are_both_kept()
    {
        var (server, a, b, time) = await StartAsync("Hello world");
        await a.Replica.Edit().InsertText(Note, "body", 5, ",").CommitAsync();          // "Hello, world"
        time.Now = time.Now.AddSeconds(1);
        await b.Replica.Edit().InsertText(Note, "body", 11, "!").CommitAsync();         // "Hello world!"

        await SyncAllAsync(a, b);

        Assert.Equal("Hello, world!", server.Find(Note)!.Text("body"));
        Assert.Equal("Hello, world!", a.Replica.Find(Note)!.Text("body"));
        Assert.Equal("Hello, world!", b.Replica.Find(Note)!.Text("body"));
    }

    [Fact]
    public async Task Insertions_at_one_place_never_interleave_and_have_one_order_everywhere()
    {
        var (server, a, b, time) = await StartAsync("[]");
        await a.Replica.Edit().InsertText(Note, "body", 1, "alpha").CommitAsync();
        time.Now = time.Now.AddSeconds(1);
        await b.Replica.Edit().InsertText(Note, "body", 1, "beta").CommitAsync();

        await SyncAllAsync(b, a);

        Assert.Equal("[betaalpha]", server.Find(Note)!.Text("body"));     // the later first, each whole
        Assert.Equal(server.Find(Note)!.Text("body"), a.Replica.Find(Note)!.Text("body"));
        Assert.Equal(server.Find(Note)!.Text("body"), b.Replica.Find(Note)!.Text("body"));
    }

    [Fact]
    public async Task Text_typed_inside_a_passage_erased_concurrently_survives()
    {
        var (server, a, b, _) = await StartAsync("one two three");
        await a.Replica.Edit().EraseText(Note, "body", 3, 4).CommitAsync();              // "one three"
        await b.Replica.Edit().InsertText(Note, "body", 5, "-and-a-half").CommitAsync(); // "one tw-and-a-halfo three"

        await SyncAllAsync(a, b);

        Assert.Equal("one-and-a-half three", server.Find(Note)!.Text("body"));
        Assert.Equal(server.Find(Note)!.Text("body"), a.Replica.Find(Note)!.Text("body"));
        Assert.Equal(server.Find(Note)!.Text("body"), b.Replica.Find(Note)!.Text("body"));
    }

    [Fact]
    public async Task A_whole_new_value_from_a_text_box_keeps_what_others_changed_elsewhere()
    {
        var (server, a, b, _) = await StartAsync("The quick fox");
        await a.Replica.Edit().ReplaceText(Note, "body", "The quick brown fox").CommitAsync();
        await b.Replica.Edit().ReplaceText(Note, "body", "The quick fox jumps").CommitAsync();

        await SyncAllAsync(a, b);

        Assert.Equal("The quick brown fox jumps", server.Find(Note)!.Text("body"));
        Assert.Equal(server.Find(Note)!.Text("body"), b.Replica.Find(Note)!.Text("body"));
    }

    [Fact]
    public async Task The_operations_of_one_change_see_the_ones_before_them()
    {
        var server = new ReplicationServer(AllowAllReplicationPolicy.Instance);
        var a = await Device.StartAsync("A", server, collections: ["Note"]);
        await a.Replica.Edit().Create(Note)
            .InsertText(Note, "body", 0, "ac")
            .InsertText(Note, "body", 1, "b")
            .EraseText(Note, "body", 2, 1)
            .AddElement(Note, "tags", "x").RemoveElement(Note, "tags", "x")
            .CommitAsync();
        Assert.Equal("ab", a.Replica.Find(Note)!.Text("body"));
        Assert.Empty(a.Replica.Find(Note)!.Elements("tags"));

        await a.SyncAsync();
        Assert.Equal("ab", server.Find(Note)!.Text("body"));
        Assert.Empty(server.Find(Note)!.Elements("tags"));
    }

    [Fact]
    public async Task A_snapshot_keeps_the_erased_characters_a_later_insertion_refers_to()
    {
        var (server, a, b, _) = await StartAsync("abcdef");
        // B inserts after "c" offline, while A erases "c" and the log is compacted.
        b.Network.Down = true;
        await b.Replica.Edit().InsertText(Note, "body", 3, "X").CommitAsync();
        await a.Replica.Edit().EraseText(Note, "body", 2, 1).CommitAsync();
        await a.SyncAsync();
        await server.CompactAsync(server.Position);

        var late = await Device.StartAsync("C", server, collections: ["Note"]);
        Assert.True((await late.SyncAsync()).Snapshot);
        b.Network.Down = false;
        await SyncAllAsync(a, b, late);

        Assert.Equal("abXdef", server.Find(Note)!.Text("body"));
        Assert.All(new[] { a, b, late }, d => Assert.Equal("abXdef", d.Replica.Find(Note)!.Text("body")));
    }

    [Fact]
    public async Task Edits_of_text_written_under_a_wrong_clock_still_refer_to_it_once_stamped_again()
    {
        var serverTime = new ManualTime(DateTimeOffset.UnixEpoch.AddDays(20_000));
        var deviceTime = new ManualTime(serverTime.Now.AddDays(3));
        var server = new ReplicationServer(AllowAllReplicationPolicy.Instance, time: serverTime);
        var a = await Device.StartAsync("A", server, collections: ["Note"], time: deviceTime);
        await a.Replica.Edit().Create(Note).InsertText(Note, "body", 0, "abc").CommitAsync();
        await a.Replica.Edit().InsertText(Note, "body", 3, "d").EraseText(Note, "body", 0, 1).CommitAsync();

        deviceTime.Now = serverTime.Now;
        await a.SyncAsync();

        Assert.Equal("bcd", server.Find(Note)!.Text("body"));
        Assert.Equal("bcd", a.Replica.Find(Note)!.Text("body"));
    }

    [Fact]
    public async Task A_text_survives_a_restart()
    {
        var (_, a, _, _) = await StartAsync("abc");
        a.Network.Down = true;
        await a.Replica.Edit().EraseText(Note, "body", 0, 1).InsertText(Note, "body", 2, "d").CommitAsync();
        await a.RestartAsync();
        Assert.Equal("bcd", a.Replica.Find(Note)!.Text("body"));
    }

    [Fact]
    public async Task Positions_count_UTF16_code_units_and_a_whole_new_value_never_splits_a_character()
    {
        var server = new ReplicationServer(AllowAllReplicationPolicy.Instance);
        var a = await Device.StartAsync("A", server, collections: ["Note"]);
        await a.Replica.Edit().Create(Note).InsertText(Note, "body", 0, "a\U0001F600b").CommitAsync();   // a 😀 b: 4 code units
        await a.Replica.Edit().ReplaceText(Note, "body", "a\U0001F601b").CommitAsync();
        await a.SyncAsync();
        Assert.Equal("a\U0001F601b", server.Find(Note)!.Text("body"));
        Assert.Equal(4, server.Find(Note)!.TextIds("body").Count);
    }

    [Fact]
    public async Task Random_typing_on_three_devices_converges()
    {
        for (var seed = 1; seed <= 10; seed++)
        {
            var random = new Random(seed);
            var time = new ManualTime(DateTimeOffset.UnixEpoch.AddDays(20_000));
            var server = new ReplicationServer(AllowAllReplicationPolicy.Instance, time: time);
            var devices = new List<Device>();
            for (var i = 0; i < 3; i++)
                devices.Add(await Device.StartAsync($"D{i}", server, collections: ["Note"], time: time));
            await devices[0].Replica.Edit().Create(Note).InsertText(Note, "body", 0, "seed").CommitAsync();
            await devices[0].SyncAsync();

            for (var step = 0; step < 300; step++)
            {
                time.Now = time.Now.AddMilliseconds(random.Next(3));
                var device = devices[random.Next(devices.Count)];
                var text = device.Replica.Find(Note)?.Text("body") ?? string.Empty;
                switch (random.Next(6))
                {
                    case 0:
                        await device.TrySyncAsync();
                        break;
                    case 1:
                        device.Network.Down = !device.Network.Down;
                        break;
                    case 2 when text.Length > 0:
                        var at = random.Next(text.Length);
                        await device.Replica.Edit().EraseText(Note, "body", at, Math.Min(1 + random.Next(3), text.Length - at)).CommitAsync();
                        break;
                    case 3:
                        await device.RestartAsync();
                        break;
                    default:
                        await device.Replica.Edit().InsertText(Note, "body", random.Next(text.Length + 1), $"{(char)('a' + random.Next(26))}{random.Next(10)}").CommitAsync();
                        break;
                }
            }
            foreach (var device in devices)
                device.Network.Down = false;
            await SyncAllAsync([.. devices]);

            var expected = server.Find(Note)!.Text("body");
            Assert.All(devices, d => Assert.Equal(expected, d.Replica.Find(Note)!.Text("body")));
        }
    }

    // Audit of 17 November 2026, B2: a text is kept in chunks shared between clones. Edits across their boundaries, and clones taken
    // between edits, must read as a plain string would.
    [Fact]
    public void A_long_text_edited_across_its_chunks_reads_as_a_string_and_its_clones_do_not_move()
    {
        var random = new Random(7);
        var replica = ReplicaId.New();
        var state = new DocumentState(Note);
        var expected = new System.Text.StringBuilder();
        var clones = new List<(DocumentState State, string Text)>();
        var seq = 0;
        for (var step = 0; step < 3000; step++)
        {
            var stamp = new OpStamp(new Hlc(1000 + step, 0, replica), 0);
            var dot = new Dot(replica, ++seq);
            var ids = state.TextIds("body");
            if (ids.Count > 0 && random.Next(4) == 0)
            {
                var at = random.Next(ids.Count);
                var length = Math.Min(ids.Count - at, random.Next(1, 300));
                state.Apply(new EraseText("body", ChangeBuilder.Spans(ids.Skip(at).Take(length))), stamp, dot);
                expected.Remove(at, length);
            }
            else
            {
                var at = random.Next(ids.Count + 1);
                var text = new string((char)('a' + step % 26), random.Next(1, step % 50 == 0 ? 3000 : 20));
                state.Apply(new InsertText("body", at == 0 ? null : ids[at - 1], text), stamp, dot);
                expected.Insert(at, text);
            }
            if (step % 97 == 0)
                clones.Add((state.Clone(), expected.ToString()));
        }

        Assert.Equal(expected.ToString(), state.Text("body"));
        Assert.All(clones, c => Assert.Equal(c.Text, c.State.Text("body")));
        Assert.Equal(expected.ToString(), DocumentState.FromSnapshot(state.ToSnapshot()).Text("body"));
    }

    // S2: a change of many erasures on a long text is refused before it holds the server.
    [Fact]
    public async Task A_change_that_would_read_a_long_text_too_many_times_is_refused()
    {
        var server = new ReplicationServer(AllowAllReplicationPolicy.Instance, new ReplicationServerOptions { MaxTextWorkPerChange = 100_000 });
        var a = await Device.StartAsync("A", server, collections: ["Note"]);
        await a.Replica.Edit().Create(Note).InsertText(Note, "body", 0, new string('x', 10_000)).CommitAsync();
        await a.SyncAsync();

        var change = a.Replica.Edit();
        for (var i = 0; i < 20; i++)
            change.EraseText(Note, "body", 0, 1);
        await change.CommitAsync();
        var report = await a.SyncAsync();

        Assert.Equal(1, report.Rejected);
        Assert.Equal(RemoteErrorCodes.TooLarge, Assert.Single(a.Replica.Rejected).Code);
        Assert.Equal(10_000, server.Find(Note)!.Text("body").Length);
        Assert.Equal(10_000, a.Replica.Find(Note)!.Text("body").Length);     // its effect is gone

        await a.Replica.Edit().EraseText(Note, "body", 0, 1).CommitAsync();    // a keystroke still goes through
        Assert.Equal(0, (await a.SyncAsync()).Rejected);
    }
}

