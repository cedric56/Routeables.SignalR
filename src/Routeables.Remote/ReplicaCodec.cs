using System.Text;

namespace Routeables.Remote;

/// <summary>
/// The stored state of a replica, in a binary format of its own: versioned, without reflection (Native AOT), and
/// independent of the serializer of the transport.
/// </summary>
public static class ReplicaCodec
{
    // 2: text fields. Version 1 (no text) is still read.
    private const byte Version = 2;

    internal sealed record StoredReplica(
        ReplicaId Id,
        Hlc Clock,
        long NextSeq,
        long Cursor,
        IReadOnlyList<string> Collections,
        IReadOnlyList<DocumentSnapshot> Documents,
        IReadOnlyList<Dot> Applied,
        IReadOnlyList<(Change Change, long Position)> Pending,
        IReadOnlyList<RejectedChange> Rejected);

    internal static byte[] Write(StoredReplica replica)
    {
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Version);
            WriteReplica(w, replica.Id);
            WriteHlc(w, replica.Clock);
            w.Write(replica.NextSeq);
            w.Write(replica.Cursor);
            WriteList(w, replica.Collections, w.Write);
            WriteList(w, replica.Documents, d => WriteDocument(w, d));
            WriteList(w, replica.Applied, d => WriteDot(w, d));
            WriteList(w, replica.Pending, p =>
            {
                WriteChange(w, p.Change);
                w.Write(p.Position);
            });
            WriteList(w, replica.Rejected, r =>
            {
                WriteChange(w, r.Change);
                WriteNullable(w, r.Code);
                WriteNullable(w, r.Message);
            });
        }
        return stream.ToArray();
    }

    [ThreadStatic]
    private static byte _reading;

    internal static StoredReplica? Read(byte[]? data)
    {
        if (data is not { Length: > 0 })
            return null;
        using var r = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
        var version = r.ReadByte();
        if (version is < 1 or > Version)
            throw new InvalidDataException($"The replica was stored by version {version} of the format; this one reads 1 to {Version}.");
        _reading = version;
        return new StoredReplica(
            ReadReplica(r),
            ReadHlc(r),
            r.ReadInt64(),
            r.ReadInt64(),
            ReadList(r, r.ReadString),
            ReadList(r, () => ReadDocument(r)),
            ReadList(r, () => ReadDot(r)),
            ReadList(r, () => (ReadChange(r), r.ReadInt64())),
            ReadList(r, () => new RejectedChange(ReadChange(r), ReadNullable(r), ReadNullable(r))));
    }

    private static void WriteList<T>(BinaryWriter w, IReadOnlyList<T> items, Action<T> write)
    {
        w.Write(items.Count);
        foreach (var item in items)
            write(item);
    }

    private static List<T> ReadList<T>(BinaryReader r, Func<T> read)
    {
        var count = r.ReadInt32();
        if (count < 0)
            throw new InvalidDataException("A negative count.");
        var list = new List<T>(Math.Min(count, 4096));
        for (var i = 0; i < count; i++)
            list.Add(read());
        return list;
    }

    private static void WriteNullable(BinaryWriter w, string? value)
    {
        w.Write(value is not null);
        if (value is not null)
            w.Write(value);
    }

    private static string? ReadNullable(BinaryReader r) => r.ReadBoolean() ? r.ReadString() : null;

    private static void WriteReplica(BinaryWriter w, ReplicaId id) => w.Write(id.Value.ToByteArray());

    private static ReplicaId ReadReplica(BinaryReader r) => new(new Guid(r.ReadBytes(16)));

    private static void WriteHlc(BinaryWriter w, Hlc hlc)
    {
        w.Write(hlc.Wall);
        w.Write(hlc.Counter);
        WriteReplica(w, hlc.Replica);
    }

    private static Hlc ReadHlc(BinaryReader r) => new(r.ReadInt64(), r.ReadInt32(), ReadReplica(r));

    private static void WriteDot(BinaryWriter w, Dot dot)
    {
        WriteReplica(w, dot.Replica);
        w.Write(dot.Seq);
    }

    private static Dot ReadDot(BinaryReader r) => new(ReadReplica(r), r.ReadInt64());

    private static void WriteStamp(BinaryWriter w, OpStamp stamp)
    {
        WriteHlc(w, stamp.Stamp);
        w.Write(stamp.Index);
    }

    private static OpStamp ReadStamp(BinaryReader r) => new(ReadHlc(r), r.ReadInt32());

    private static void WriteTag(BinaryWriter w, ElementTag tag)
    {
        WriteDot(w, tag.Dot);
        w.Write(tag.Index);
    }

    private static ElementTag ReadTag(BinaryReader r) => new(ReadDot(r), r.ReadInt32());

    private static void WriteTextId(BinaryWriter w, TextId id)
    {
        WriteDot(w, id.Dot);
        w.Write(id.Index);
        w.Write(id.Offset);
    }

    private static TextId ReadTextId(BinaryReader r) => new(ReadDot(r), r.ReadInt32(), r.ReadInt32());

    private static void WriteId(BinaryWriter w, DocumentId id)
    {
        w.Write(id.Collection);
        w.Write(id.Key);
    }

    private static DocumentId ReadId(BinaryReader r) => new(r.ReadString(), r.ReadString());

    private static void WriteChange(BinaryWriter w, Change change)
    {
        WriteDot(w, change.Dot);
        WriteHlc(w, change.Stamp);
        WriteList(w, change.Ops, op =>
        {
            WriteId(w, op.Document);
            switch (op.Op)
            {
                case SetField set:
                    w.Write((byte)1);
                    w.Write(set.Field);
                    WriteNullable(w, set.Value);
                    break;
                case AddElement add:
                    w.Write((byte)2);
                    w.Write(add.Field);
                    w.Write(add.Element);
                    break;
                case RemoveElement remove:
                    w.Write((byte)3);
                    w.Write(remove.Field);
                    w.Write(remove.Element);
                    WriteList(w, remove.Observed, t => WriteTag(w, t));
                    break;
                case Increment increment:
                    w.Write((byte)4);
                    w.Write(increment.Field);
                    w.Write(increment.Delta);
                    break;
                case DeleteDocument:
                    w.Write((byte)5);
                    break;
                case RestoreDocument:
                    w.Write((byte)6);
                    break;
                case InsertText insert:
                    w.Write((byte)7);
                    w.Write(insert.Field);
                    w.Write(insert.After is not null);
                    if (insert.After is { } after)
                        WriteTextId(w, after);
                    w.Write(insert.Text);
                    break;
                case EraseText erase:
                    w.Write((byte)8);
                    w.Write(erase.Field);
                    WriteList(w, erase.Spans, span =>
                    {
                        WriteDot(w, span.Dot);
                        w.Write(span.Index);
                        w.Write(span.Offset);
                        w.Write(span.Length);
                    });
                    break;
                default:
                    throw new ArgumentException($"Unknown operation {op.Op.GetType().Name}.", nameof(change));
            }
        });
    }

    private static Change ReadChange(BinaryReader r)
    {
        var dot = ReadDot(r);
        var stamp = ReadHlc(r);
        var ops = ReadList(r, () =>
        {
            var id = ReadId(r);
            FieldOp op = r.ReadByte() switch
            {
                1 => new SetField(r.ReadString(), ReadNullable(r)),
                2 => new AddElement(r.ReadString(), r.ReadString()),
                3 => new RemoveElement(r.ReadString(), r.ReadString(), ReadList(r, () => ReadTag(r))),
                4 => new Increment(r.ReadString(), r.ReadInt64()),
                5 => new DeleteDocument(),
                6 => new RestoreDocument(),
                7 => new InsertText(r.ReadString(), r.ReadBoolean() ? ReadTextId(r) : null, r.ReadString()),
                8 => new EraseText(r.ReadString(), ReadList(r, () => new TextSpan(ReadDot(r), r.ReadInt32(), r.ReadInt32(), r.ReadInt32()))),
                var kind => throw new InvalidDataException($"Unknown operation {kind}."),
            };
            return new DocumentOp(id, op);
        });
        return new Change(dot, stamp, ops);
    }

    private static void WriteDocument(BinaryWriter w, DocumentSnapshot d)
    {
        WriteId(w, d.Id);
        w.Write(d.Exists);
        w.Write(d.ExistenceStamp is not null);
        if (d.ExistenceStamp is { } stamp)
            WriteStamp(w, stamp);
        WriteList(w, d.Registers, x =>
        {
            w.Write(x.Field);
            WriteNullable(w, x.Value);
            WriteStamp(w, x.Stamp);
        });
        WriteList(w, d.Elements, x =>
        {
            w.Write(x.Field);
            w.Write(x.Element);
            WriteList(w, x.Tags, t => WriteTag(w, t));
        });
        WriteList(w, d.Counters, x =>
        {
            w.Write(x.Field);
            w.Write(x.Value);
        });
        WriteList(w, d.Texts ?? [], t =>
        {
            w.Write(t.Field);
            WriteList(w, t.Runs, run =>
            {
                WriteTextId(w, run.Start);
                WriteStamp(w, run.Stamp);
                w.Write(run.Text);
                w.Write(run.Erased);
            });
        });
    }

    private static DocumentSnapshot ReadDocument(BinaryReader r)
    {
        var id = ReadId(r);
        var exists = r.ReadBoolean();
        OpStamp? stamp = r.ReadBoolean() ? ReadStamp(r) : null;
        return new DocumentSnapshot(
            id, exists, stamp,
            ReadList(r, () => new RegisterSnapshot(r.ReadString(), ReadNullable(r), ReadStamp(r))),
            ReadList(r, () => new ElementSnapshot(r.ReadString(), r.ReadString(), ReadList(r, () => ReadTag(r)))),
            ReadList(r, () => new CounterSnapshot(r.ReadString(), r.ReadInt64())),
            _reading < 2 ? [] : ReadList(r, () => new TextSnapshot(r.ReadString(),
                ReadList(r, () => new TextRun(ReadTextId(r), ReadStamp(r), r.ReadString(), r.ReadBoolean())))));
    }
}
