using System.Text.Json.Serialization;

namespace Routeables.Remote;

/// <summary>
/// A replicated document: one entity (Queryeables: <c>("Person", "4f1c...")</c>) or the state of one view model
/// (Routeables.SignalR: <c>("route:/orders", "session-key")</c>). <see cref="Collection"/> is what a client subscribes to
/// and what the server authorizes; <see cref="Key"/> is chosen by the replica that creates the document (a Guid), so that
/// it never changes once offline writes refer to it.
/// </summary>
public readonly record struct DocumentId(string Collection, string Key)
{
    public override string ToString() => $"{Collection}/{Key}";
}

/// <summary>
/// One operation on one field of a document. Values and elements are opaque text (JSON, by convention): the protocol
/// merges them, it does not read them.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]
[JsonDerivedType(typeof(SetField), "set")]
[JsonDerivedType(typeof(AddElement), "add")]
[JsonDerivedType(typeof(RemoveElement), "remove")]
[JsonDerivedType(typeof(Increment), "increment")]
[JsonDerivedType(typeof(DeleteDocument), "delete")]
[JsonDerivedType(typeof(RestoreDocument), "restore")]
[JsonDerivedType(typeof(InsertText), "insert")]
[JsonDerivedType(typeof(EraseText), "erase")]
public abstract record FieldOp;

/// <summary>A last-writer-wins field takes this value (null clears it): the latest stamp wins on every replica.</summary>
public sealed record SetField(string Field, string? Value) : FieldOp;

/// <summary>An element joins an observed-remove set. Added concurrently with its removal, it stays (add wins).</summary>
public sealed record AddElement(string Field, string Element) : FieldOp;

/// <summary>
/// An element leaves an observed-remove set: the additions the writer had seen (<see cref="Observed"/>) are removed; an
/// addition it had not seen is not.
/// </summary>
public sealed record RemoveElement(string Field, string Element, IReadOnlyList<ElementTag> Observed) : FieldOp;

/// <summary>A counter moves by <see cref="Delta"/>: concurrent increments add up.</summary>
public sealed record Increment(string Field, long Delta) : FieldOp;

/// <summary>The document is deleted (a tombstone): the latest of deletion and restoration wins.</summary>
public sealed record DeleteDocument : FieldOp;

/// <summary>The document exists: its creation, or its restoration after a deletion.</summary>
public sealed record RestoreDocument : FieldOp;

/// <summary>
/// Text is inserted in a text field right after the character <see cref="After"/> (at the start when null). Concurrent
/// insertions at one place keep each run whole, the latest first (RGA): two people typing in one field both keep their
/// words.
/// </summary>
public sealed record InsertText(string Field, TextId? After, string Text) : FieldOp;

/// <summary>Characters of a text field are erased, by identity: a character inserted concurrently is not.</summary>
public sealed record EraseText(string Field, IReadOnlyList<TextSpan> Spans) : FieldOp;

/// <summary>
/// The identity of one character of a text field: the insertion that made it (<see cref="Dot"/> and the rank
/// <see cref="Index"/> of the operation in its change) and its rank in the inserted text (<see cref="Offset"/>, in UTF-16
/// code units, as .NET, JavaScript and Dart count them).
/// </summary>
public readonly record struct TextId(Dot Dot, int Index, int Offset);

/// <summary><see cref="Length"/> characters of one insertion, from <see cref="Offset"/>.</summary>
public readonly record struct TextSpan(Dot Dot, int Index, int Offset, int Length);

/// <summary>The identity of one addition to a set: the change that made it and the rank of the operation in it.</summary>
public readonly record struct ElementTag(Dot Dot, int Index);

/// <summary>An operation and the document it applies to.</summary>
public sealed record DocumentOp(DocumentId Document, FieldOp Op);

/// <summary>
/// The unit of replication: what one replica did at once, stamped once. A change is admitted whole or refused whole, so
/// that a parent and its children (a Queryeables graph) or the fields one command set (a view model) never arrive in
/// part.
/// </summary>
/// <param name="Dot">Its identity.</param>
/// <param name="Stamp">When it was made: orders it against the changes of other replicas for last-writer-wins fields.</param>
/// <param name="Ops">What it does, in order.</param>
public sealed record Change(Dot Dot, Hlc Stamp, IReadOnlyList<DocumentOp> Ops);
