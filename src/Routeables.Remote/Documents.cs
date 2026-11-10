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
    private readonly Dictionary<string, List<TextNode>> _texts = new(StringComparer.Ordinal);

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
        foreach (var node in nodes)
            if (!node.Erased)
                text.Append(node.Value);
        return text.ToString();
    }

    /// <summary>The identities of the characters of a text field that are not erased, in order: what an edit refers to.</summary>
    public IReadOnlyList<TextId> TextIds(string field)
        => _texts.TryGetValue(field, out var nodes) ? [.. nodes.Where(n => !n.Erased).Select(n => n.Id)] : [];

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
                if (_texts.TryGetValue(erase.Field, out var text))
                    Erase(text, erase.Spans);
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
            _texts[insert.Field] = nodes = [];
        var first = new TextId(dot, stamp.Index, 0);
        if (nodes.Exists(n => n.Id == first))
            return;
        var at = 0;
        if (insert.After is { } after)
        {
            var anchor = nodes.FindIndex(n => n.Id == after);
            at = anchor < 0 ? 0 : anchor + 1;
        }
        while (at < nodes.Count && nodes[at].IsAfter(stamp, 0))
            at++;
        var run = new TextNode[insert.Text.Length];
        for (var offset = 0; offset < run.Length; offset++)
            run[offset] = new TextNode(new TextId(dot, stamp.Index, offset), insert.Text[offset], stamp, false);
        nodes.InsertRange(at, run);
    }

    private static void Erase(List<TextNode> nodes, IReadOnlyList<TextSpan> spans)
    {
        for (var i = 0; i < nodes.Count; i++)
        {
            var id = nodes[i].Id;
            foreach (var span in spans)
            {
                if (span.Dot == id.Dot && span.Index == id.Index && id.Offset >= span.Offset && id.Offset < span.Offset + span.Length)
                {
                    nodes[i] = nodes[i] with { Erased = true };
                    break;
                }
            }
        }
    }

    private HashSet<ElementTag> Set(string field, string element)
    {
        if (!_sets.TryGetValue(field, out var set))
            _sets[field] = set = new Dictionary<string, HashSet<ElementTag>>(StringComparer.Ordinal);
        if (!set.TryGetValue(element, out var tags))
            set[element] = tags = [];
        return tags;
    }

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
            copy._texts[name] = [.. nodes];
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
    private static List<TextRun> Runs(List<TextNode> nodes)
    {
        var runs = new List<TextRun>();
        var i = 0;
        while (i < nodes.Count)
        {
            var start = nodes[i];
            var text = new System.Text.StringBuilder().Append(start.Value);
            var j = i + 1;
            while (j < nodes.Count && nodes[j].Id.Dot == start.Id.Dot && nodes[j].Id.Index == start.Id.Index
                   && nodes[j].Id.Offset == start.Id.Offset + (j - i) && nodes[j].Erased == start.Erased)
                text.Append(nodes[j++].Value);
            runs.Add(new TextRun(start.Id, start.Stamp, text.ToString(), start.Erased));
            i = j;
        }
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
            var nodes = state._texts[text.Field] = [];
            foreach (var run in text.Runs)
                for (var offset = 0; offset < run.Text.Length; offset++)
                    nodes.Add(new TextNode(run.Start with { Offset = run.Start.Offset + offset }, run.Text[offset], run.Stamp, run.Erased));
        }
        return state;
    }

    private readonly record struct Register(string? Value, OpStamp Stamp);

    private readonly record struct TextNode(TextId Id, char Value, OpStamp Stamp, bool Erased)
    {
        // Characters are ordered by the stamp of their insertion, then by their rank in it.
        public bool IsAfter(OpStamp stamp, int offset)
        {
            var order = Stamp.CompareTo(stamp);
            return order > 0 || order == 0 && Id.Offset > offset;
        }
    }
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
