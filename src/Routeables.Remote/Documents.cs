using System.Collections.Immutable;

namespace Routeables.Remote;

/// <summary>The stamp of one operation: the stamp of its change, then its rank in the change.</summary>
public readonly record struct OpStamp(Hlc Stamp, int Index) : IComparable<OpStamp>
{
    public int CompareTo(OpStamp other)
    {
        var stamp = Stamp.CompareTo(other.Stamp);
        return stamp != 0 ? stamp : Index.CompareTo(other.Index);
    }

    public static bool operator >(OpStamp left, OpStamp right) => left.CompareTo(right) > 0;

    public static bool operator <(OpStamp left, OpStamp right) => left.CompareTo(right) < 0;
}

/// <summary>
/// The merged state of one document: every field with what is needed to merge later operations (stamps, tags). Applying
/// the same operations in any order that keeps each replica's own order gives the same state.
/// </summary>
public sealed class DocumentState
{
    private readonly Dictionary<string, Register> _registers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, HashSet<ElementTag>>> _sets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _counters = new(StringComparer.Ordinal);
    // In chunks shared with the clones until written (TextNodes): a clone costs the number of chunks, a keystroke copies one chunk.
    private readonly Dictionary<string, TextNodes> _texts = new(StringComparer.Ordinal);
    // The insertions of each text (their dot and rank) and how many characters each one made, built when an insertion first needs
    // them: whether a character is known costs nothing. Immutable, so a clone shares it as it is.
    private readonly Dictionary<string, ImmutableDictionary<(Dot Dot, int Index), int>> _insertions = new(StringComparer.Ordinal);
    // Where the last insertion of each text ended: typing anchors the next character there, found without a search.
    private readonly Dictionary<string, int> _lastInsert = new(StringComparer.Ordinal);

    public DocumentState(DocumentId id) => Id = id;

    public DocumentId Id { get; }

    /// <summary>Whether the document exists: created or restored after its last deletion.</summary>
    public bool Exists { get; private set; }

    /// <summary>The stamp of the creation, deletion or restoration that decided <see cref="Exists"/>.</summary>
    public OpStamp? ExistenceStamp { get; private set; }

    public IEnumerable<string> RegisterNames => _registers.Keys;

    /// <summary>The sets with at least one element.</summary>
    public IEnumerable<string> SetNames => _sets.Where(s => s.Value.Values.Any(tags => tags.Count > 0)).Select(s => s.Key);

    public IEnumerable<string> CounterNames => _counters.Keys;

    public IEnumerable<string> TextNames => _texts.Keys;

    /// <summary>The value of a last-writer-wins field, null when never set or cleared.</summary>
    public string? Get(string field) => _registers.TryGetValue(field, out var register) ? register.Value : null;

    /// <summary>The stamp of the value of a field, null when never set.</summary>
    public OpStamp? StampOf(string field) => _registers.TryGetValue(field, out var register) ? register.Stamp : null;

    /// <summary>The elements of a set, in ordinal order.</summary>
    public IReadOnlyList<string> Elements(string field)
        => _sets.TryGetValue(field, out var set) ? [.. set.Where(e => e.Value.Count > 0).Select(e => e.Key).Order(StringComparer.Ordinal)] : [];

    /// <summary>The additions of an element that are still in the set: what a removal observes.</summary>
    public IReadOnlyList<ElementTag> TagsOf(string field, string element)
        => _sets.TryGetValue(field, out var set) && set.TryGetValue(element, out var tags) ? [.. tags] : [];

    /// <summary>The value of a counter.</summary>
    public long Counter(string field) => _counters.GetValueOrDefault(field);

    /// <summary>The text of a text field: its characters not erased, in order.</summary>
    public string Text(string field)
    {
        if (!_texts.TryGetValue(field, out var nodes))
            return string.Empty;
        var text = new System.Text.StringBuilder(nodes.Count);
        for (var c = 0; c < nodes.ChunkCount; c++)
            foreach (var node in nodes.ChunkAt(c))
                if (!node.Erased)
                    text.Append(node.Value);
        return text.ToString();
    }

    /// <summary>How many characters a text field holds, the erased ones included: what it weighs.</summary>
    public int TextSize(string field) => _texts.TryGetValue(field, out var nodes) ? nodes.Count : 0;

    /// <summary>The identities of the characters of a text field that are not erased, in order: what an edit refers to.</summary>
    public IReadOnlyList<TextId> TextIds(string field)
        => _texts.TryGetValue(field, out var nodes) ? [.. nodes.Where(n => !n.Erased).Select(n => n.Id)] : [];

    /// <summary>
    /// How many characters of a text field are not erased, and the identities of those from <paramref name="start"/> on, at most
    /// <paramref name="count"/>: what an edit refers to, read in one pass without copying the identities of the whole text.
    /// </summary>
    internal int VisibleIds(string field, int start, int count, List<TextId>? into)
    {
        if (!_texts.TryGetValue(field, out var nodes))
            return 0;
        var visible = 0;
        for (var c = 0; c < nodes.ChunkCount; c++)
        {
            foreach (var node in nodes.ChunkAt(c))
            {
                if (node.Erased)
                    continue;
                if (into is not null && visible >= start && visible - start < count)
                    into.Add(node.Id);
                visible++;
            }
        }
        return visible;
    }

    internal void Apply(FieldOp op, OpStamp stamp, Dot dot)
    {
        switch (op)
        {
            case SetField set:
                if (!_registers.TryGetValue(set.Field, out var register) || stamp > register.Stamp)
                    _registers[set.Field] = new Register(set.Value, stamp);
                break;
            case AddElement add:
                Set(add.Field, add.Element).Add(new ElementTag(dot, stamp.Index));
                break;
            case RemoveElement remove:
                if (_sets.TryGetValue(remove.Field, out var elements) && elements.TryGetValue(remove.Element, out var tags))
                    tags.ExceptWith(remove.Observed);
                break;
            case Increment increment:
                _counters[increment.Field] = _counters.GetValueOrDefault(increment.Field) + increment.Delta;
                break;
            case InsertText insert:
                Insert(insert, stamp, dot);
                break;
            case EraseText erase:
                if (_texts.ContainsKey(erase.Field))
                    Erase(erase.Field, erase.Spans);
                break;
            case DeleteDocument or RestoreDocument:
                if (ExistenceStamp is not { } current || stamp > current)
                {
                    Exists = op is RestoreDocument;
                    ExistenceStamp = stamp;
                }
                break;
            default:
                throw new ArgumentException($"Unknown operation {op.GetType().Name}.", nameof(op));
        }
    }

    // RGA: right after the anchor, past the characters inserted there by later insertions, so that concurrent runs at one
    // place never interleave and the latest comes first. The anchor is missing only when its insertion was refused: the
    // same everywhere, so the run goes to the start everywhere.
    private void Insert(InsertText insert, OpStamp stamp, Dot dot)
    {
        if (insert.Text.Length == 0)
            return;
        if (!_texts.TryGetValue(insert.Field, out var nodes))
            _texts[insert.Field] = nodes = new TextNodes();
        var ids = Insertions(insert.Field, nodes);
        if (ids.ContainsKey((dot, stamp.Index)))
            return;
        var at = 0;
        if (insert.After is { } after && ids.TryGetValue((after.Dot, after.Index), out var length) && after.Offset >= 0 && after.Offset < length)
            at = (_lastInsert.TryGetValue(insert.Field, out var last) && last >= 0 && last < nodes.Count && nodes[last].Id == after
                ? last
                : nodes.IndexOf(after)) + 1;
        at = nodes.SkipAfter(at, stamp);
        var run = new TextNode[insert.Text.Length];
        for (var offset = 0; offset < run.Length; offset++)
            run[offset] = new TextNode(new TextId(dot, stamp.Index, offset), insert.Text[offset], stamp, false);
        nodes.Insert(at, run);
        _insertions[insert.Field] = ids.Add((dot, stamp.Index), run.Length);
        _lastInsert[insert.Field] = at + run.Length - 1;
    }

    // The insertions of a text, built from its characters the first time they are needed.
    private ImmutableDictionary<(Dot Dot, int Index), int> Insertions(string field, TextNodes nodes)
    {
        if (_insertions.TryGetValue(field, out var ids))
            return ids;
        var builder = ImmutableDictionary.CreateBuilder<(Dot Dot, int Index), int>();
        foreach (var node in nodes)
        {
            var key = (node.Id.Dot, node.Id.Index);
            if (!builder.TryGetValue(key, out var length) || length <= node.Id.Offset)
                builder[key] = node.Id.Offset + 1;
        }
        return _insertions[field] = builder.ToImmutable();
    }

    // One pass over the text: the spans are looked up by the insertion they belong to. Few insertions (a key pressed, a word
    // erased) are compared directly, without hashing every character of the text.
    private void Erase(string field, IReadOnlyList<TextSpan> spans)
    {
        if (spans.Count == 0)
            return;
        var nodes = _texts[field];
        var byInsertion = new Dictionary<(Dot, int), List<TextSpan>>();
        foreach (var span in spans)
        {
            if (!byInsertion.TryGetValue((span.Dot, span.Index), out var list))
                byInsertion[(span.Dot, span.Index)] = list = [];
            list.Add(span);
        }
        var few = byInsertion.Count <= 8 ? byInsertion.Select(b => (b.Key.Item1, b.Key.Item2, b.Value)).ToArray() : null;
        nodes.Erase(node =>
        {
            List<TextSpan>? found = null;
            if (few is not null)
            {
                foreach (var (dot, index, list) in few)
                {
                    if (index == node.Id.Index && dot == node.Id.Dot)
                    {
                        found = list;
                        break;
                    }
                }
            }
            else
            {
                byInsertion.TryGetValue((node.Id.Dot, node.Id.Index), out found);
            }
            if (found is null)
                return false;
            foreach (var span in found)
                if (node.Id.Offset >= span.Offset && node.Id.Offset < span.Offset + span.Length)
                    return true;
            return false;
        });
    }

    private HashSet<ElementTag> Set(string field, string element)
    {
        if (!_sets.TryGetValue(field, out var set))
            _sets[field] = set = new Dictionary<string, HashSet<ElementTag>>(StringComparer.Ordinal);
        if (!set.TryGetValue(element, out var tags))
            set[element] = tags = [];
        return tags;
    }

    /// <summary>
    /// A copy, independent of this state. Its texts are shared until one of the two changes them (copy on write): a clone costs the
    /// number of fields and elements, not the length of the texts.
    /// </summary>
    public DocumentState Clone()
    {
        var copy = new DocumentState(Id) { Exists = Exists, ExistenceStamp = ExistenceStamp };
        foreach (var (name, register) in _registers)
            copy._registers[name] = register;
        foreach (var (name, set) in _sets)
            copy._sets[name] = set.ToDictionary(e => e.Key, e => new HashSet<ElementTag>(e.Value), StringComparer.Ordinal);
        foreach (var (name, value) in _counters)
            copy._counters[name] = value;
        foreach (var (name, nodes) in _texts)
            copy._texts[name] = nodes.Clone();
        foreach (var (name, ids) in _insertions)
            copy._insertions[name] = ids;
        foreach (var (name, last) in _lastInsert)
            copy._lastInsert[name] = last;
        return copy;
    }

    /// <summary>The state as data, for a snapshot or for storage.</summary>
    public DocumentSnapshot ToSnapshot() => new(
        Id,
        Exists,
        ExistenceStamp,
        [.. _registers.Select(r => new RegisterSnapshot(r.Key, r.Value.Value, r.Value.Stamp))],
        [.. _sets.SelectMany(s => s.Value.Where(e => e.Value.Count > 0).Select(e => new ElementSnapshot(s.Key, e.Key, [.. e.Value])))],
        [.. _counters.Select(c => new CounterSnapshot(c.Key, c.Value))],
        [.. _texts.Select(t => new TextSnapshot(t.Key, Runs(t.Value)))]);

    // Consecutive characters of one insertion, alike erased or not, are stored as one run.
    private static List<TextRun> Runs(TextNodes nodes)
    {
        var runs = new List<TextRun>();
        var text = new System.Text.StringBuilder();
        TextNode start = default;
        var length = 0;
        foreach (var node in nodes)
        {
            if (length > 0 && node.Id.Dot == start.Id.Dot && node.Id.Index == start.Id.Index && node.Id.Offset == start.Id.Offset + length
                && node.Erased == start.Erased)
            {
                text.Append(node.Value);
                length++;
                continue;
            }
            if (length > 0)
                runs.Add(new TextRun(start.Id, start.Stamp, text.ToString(), start.Erased));
            start = node;
            text.Clear().Append(node.Value);
            length = 1;
        }
        if (length > 0)
            runs.Add(new TextRun(start.Id, start.Stamp, text.ToString(), start.Erased));
        return runs;
    }

    public static DocumentState FromSnapshot(DocumentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var state = new DocumentState(snapshot.Id) { Exists = snapshot.Exists, ExistenceStamp = snapshot.ExistenceStamp };
        foreach (var register in snapshot.Registers)
            state._registers[register.Field] = new Register(register.Value, register.Stamp);
        foreach (var element in snapshot.Elements)
            state.Set(element.Field, element.Element).UnionWith(element.Tags);
        foreach (var counter in snapshot.Counters)
            state._counters[counter.Field] = counter.Value;
        foreach (var text in snapshot.Texts ?? [])
        {
            var nodes = state._texts[text.Field] = new TextNodes();
            foreach (var run in text.Runs)
                for (var offset = 0; offset < run.Text.Length; offset++)
                    nodes.Add(new TextNode(run.Start with { Offset = run.Start.Offset + offset }, run.Text[offset], run.Stamp, run.Erased));
        }
        return state;
    }

    private readonly record struct Register(string? Value, OpStamp Stamp);

}

/// <summary>One character of a text field, erased or not.</summary>
internal readonly record struct TextNode(TextId Id, char Value, OpStamp Stamp, bool Erased)
{
    // Characters are ordered by the stamp of their insertion, then by their rank in it.
    public bool IsAfter(OpStamp stamp, int offset)
    {
        var order = Stamp.CompareTo(stamp);
        return order > 0 || order == 0 && Id.Offset > offset;
    }
}

/// <summary>
/// The characters of a text field, in order, in chunks of at most <see cref="MaxChunk"/>. A clone shares the chunks, and each side copies
/// a chunk before it changes it (copy on write): cloning a document costs the number of chunks, a keystroke copies one chunk, and the
/// cost of an edit no longer grows with the length of the text (audit of 17 November 2026, B2). Not safe for a write while another thread
/// clones the same instance: a document has one owner at a time (a replica, a decision of the server, a store under its lock).
/// </summary>
internal sealed class TextNodes : IEnumerable<TextNode>
{
    internal const int MaxChunk = 1024;

    private sealed class Chunk(List<TextNode> nodes)
    {
        public readonly List<TextNode> Nodes = nodes;
        // Seen by more than one TextNodes: copied by whichever changes it first.
        public volatile bool Shared;
    }

    private readonly List<Chunk> _chunks;

    public TextNodes() => _chunks = [];

    private TextNodes(List<Chunk> chunks, int count)
    {
        _chunks = chunks;
        Count = count;
    }

    public int Count { get; private set; }

    public int ChunkCount => _chunks.Count;

    /// <summary>The characters of a chunk, to read them: never changed through it.</summary>
    public List<TextNode> ChunkAt(int chunk) => _chunks[chunk].Nodes;

    public TextNodes Clone()
    {
        foreach (var chunk in _chunks)
            chunk.Shared = true;
        return new TextNodes([.. _chunks], Count);
    }

    // The chunk of a position, and the position in it; the end of the last chunk for Count.
    private (int Chunk, int Offset) Locate(int index)
    {
        for (var c = 0; c < _chunks.Count; c++)
        {
            var count = _chunks[c].Nodes.Count;
            if (index < count)
                return (c, index);
            index -= count;
        }
        return _chunks.Count == 0 ? (-1, 0) : (_chunks.Count - 1, _chunks[^1].Nodes.Count);
    }

    public TextNode this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
            var (chunk, offset) = Locate(index);
            return _chunks[chunk].Nodes[offset];
        }
    }

    private List<TextNode> Writable(int chunk)
    {
        var current = _chunks[chunk];
        if (!current.Shared)
            return current.Nodes;
        var copy = new List<TextNode>(Math.Max(current.Nodes.Count + 16, MaxChunk / 2));
        copy.AddRange(current.Nodes);
        _chunks[chunk] = new Chunk(copy);
        return copy;
    }

    /// <summary>Inserts characters at a position (0 to <see cref="Count"/>).</summary>
    public void Insert(int at, TextNode[] run)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(at);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(at, Count);
        if (run.Length == 0)
            return;
        if (_chunks.Count == 0)
            _chunks.Add(new Chunk(new List<TextNode>(Math.Min(MaxChunk, Math.Max(16, run.Length)))));
        var (chunk, offset) = Locate(at);
        var nodes = Writable(chunk);
        nodes.InsertRange(offset, run);
        Count += run.Length;
        if (nodes.Count > MaxChunk)
            Split(chunk);
    }

    /// <summary>Adds characters at the end (a text read from a snapshot).</summary>
    public void Add(TextNode node)
    {
        if (_chunks.Count == 0 || _chunks[^1].Nodes.Count >= MaxChunk || _chunks[^1].Shared)
            _chunks.Add(new Chunk(new List<TextNode>(MaxChunk)));
        _chunks[^1].Nodes.Add(node);
        Count++;
    }

    // A chunk grown beyond the limit becomes chunks of half of it: room for the next keystrokes in each.
    private void Split(int chunk)
    {
        var nodes = _chunks[chunk].Nodes;
        var pieces = new List<Chunk>();
        for (var start = 0; start < nodes.Count; start += MaxChunk / 2)
        {
            var count = Math.Min(MaxChunk / 2, nodes.Count - start);
            var piece = new List<TextNode>(MaxChunk);
            piece.AddRange(nodes.GetRange(start, count));
            pieces.Add(new Chunk(piece));
        }
        _chunks.RemoveAt(chunk);
        _chunks.InsertRange(chunk, pieces);
    }

    /// <summary>The position of the first character from <paramref name="at"/> that is not inserted after <paramref name="stamp"/> (RGA).</summary>
    public int SkipAfter(int at, OpStamp stamp)
    {
        if (at >= Count)
            return at;
        var (chunk, offset) = Locate(at);
        for (var c = chunk; c < _chunks.Count; c++, offset = 0)
        {
            var nodes = _chunks[c].Nodes;
            for (var i = offset; i < nodes.Count; i++, at++)
                if (!nodes[i].IsAfter(stamp, 0))
                    return at;
        }
        return at;
    }

    /// <summary>The position of a character, -1 when it is not there.</summary>
    public int IndexOf(TextId id)
    {
        var index = 0;
        foreach (var chunk in _chunks)
        {
            var nodes = chunk.Nodes;
            for (var i = 0; i < nodes.Count; i++, index++)
                if (nodes[i].Id == id)
                    return index;
        }
        return -1;
    }

    /// <summary>Marks erased the characters not erased yet that <paramref name="erases"/> selects: only the chunks that hold one are copied.</summary>
    public void Erase(Func<TextNode, bool> erases)
    {
        for (var c = 0; c < _chunks.Count; c++)
        {
            var nodes = _chunks[c].Nodes;
            var writable = false;
            for (var i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                if (node.Erased || !erases(node))
                    continue;
                if (!writable)
                {
                    nodes = Writable(c);
                    writable = true;
                }
                nodes[i] = node with { Erased = true };
            }
        }
    }

    public IEnumerator<TextNode> GetEnumerator()
    {
        foreach (var chunk in _chunks)
            foreach (var node in chunk.Nodes)
                yield return node;
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>A document as data: what a snapshot carries, and what a replica stores.</summary>
public sealed record DocumentSnapshot(
    DocumentId Id,
    bool Exists,
    OpStamp? ExistenceStamp,
    IReadOnlyList<RegisterSnapshot> Registers,
    IReadOnlyList<ElementSnapshot> Elements,
    IReadOnlyList<CounterSnapshot> Counters,
    IReadOnlyList<TextSnapshot>? Texts = null);

public sealed record RegisterSnapshot(string Field, string? Value, OpStamp Stamp);

public sealed record ElementSnapshot(string Field, string Element, IReadOnlyList<ElementTag> Tags);

public sealed record CounterSnapshot(string Field, long Value);

public sealed record TextSnapshot(string Field, IReadOnlyList<TextRun> Runs);

/// <summary>Characters of one insertion, from <see cref="Start"/>, in the order of the field, all erased or none.</summary>
public sealed record TextRun(TextId Start, OpStamp Stamp, string Text, bool Erased);

/// <summary>
/// Documents and the changes applied to them, each once. Changes come from the log of the server, where the changes of
/// one replica are in the order of their seq: the last seq applied per replica (a version vector, as long as the number
/// of replicas, not of changes) tells whether a change was applied.
/// </summary>
public sealed class DocumentSet
{
    private readonly Dictionary<DocumentId, DocumentState> _documents = [];
    private readonly Dictionary<ReplicaId, long> _applied = [];

    public IReadOnlyCollection<DocumentState> Documents => _documents.Values;

    public int Count => _documents.Count;

    public DocumentState? Find(DocumentId id) => _documents.GetValueOrDefault(id);

    public bool HasApplied(Dot dot) => _applied.TryGetValue(dot.Replica, out var seq) && dot.Seq <= seq;

    /// <summary>Applies a change, or the operations of it that <paramref name="filter"/> keeps; false when it was already applied.</summary>
    public bool Apply(Change change, Func<DocumentOp, bool>? filter = null)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (HasApplied(change.Dot))
            return false;
        _applied[change.Dot.Replica] = change.Dot.Seq;
        for (var index = 0; index < change.Ops.Count; index++)
        {
            var op = change.Ops[index];
            if (filter is not null && !filter(op))
                continue;
            if (!_documents.TryGetValue(op.Document, out var document))
                _documents[op.Document] = document = new DocumentState(op.Document);
            document.Apply(op.Op, new OpStamp(change.Stamp, index), change.Dot);
        }
        return true;
    }

    /// <summary>Replaces the documents of <paramref name="collections"/> by those of a snapshot, and forgets what was applied.</summary>
    internal void Replace(IEnumerable<DocumentSnapshot> documents, IReadOnlySet<string> collections)
    {
        foreach (var id in _documents.Keys.Where(k => collections.Contains(k.Collection)).ToList())
            _documents.Remove(id);
        foreach (var snapshot in documents)
            _documents[snapshot.Id] = DocumentState.FromSnapshot(snapshot);
        _applied.Clear();
    }

    internal void Load(IEnumerable<DocumentSnapshot> documents, IEnumerable<Dot> applied)
    {
        foreach (var snapshot in documents)
            _documents[snapshot.Id] = DocumentState.FromSnapshot(snapshot);
        foreach (var dot in applied)
            _applied[dot.Replica] = Math.Max(dot.Seq, _applied.GetValueOrDefault(dot.Replica));
    }

    /// <summary>The last seq applied per replica, as dots.</summary>
    internal IEnumerable<Dot> Applied => _applied.Select(a => new Dot(a.Key, a.Value));
}
