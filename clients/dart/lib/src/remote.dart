// The Routeables Remote Protocol (PROTOCOL-REMOTE.md), client side, for Dart and Flutter: documents replicated with a server,
// written offline at once, merged as the .NET implementation (src/Routeables.Remote) and the TypeScript client merge them. The
// fixture clients/fixtures/remote-v1.json checks that all three merge alike.
import 'dart:async';
import 'dart:convert';
import 'dart:math';

/// The version of the messages: a server of another version refuses the client.
const int remoteProtocolVersion = 1;

/// The codes of a refused change.
abstract final class RemoteErrorCodes {
  static const forbidden = 'forbidden';
  static const invalid = 'invalid';
  static const tooLarge = 'too-large';
  static const clockSkew = 'clock-skew';
  static const replicaTaken = 'replica-taken';
  static const outOfOrder = 'out-of-order';
  static const lost = 'lost';

  /// The user cannot be told apart from another one (anonymous, or without an identifier): he reads, and writes nothing.
  static const unidentified = 'unidentified';
}

typedef Json = Map<String, Object?>;

Json _map(Object? value) => (value as Map).cast<String, Object?>();
List<Object?> _list(Object? value) => value == null ? const [] : (value as List).cast<Object?>();
int _int(Object? value) => (value as num).toInt();

// ------------------------------------------------------------------------------------------------------------------ identities

/// A new replica: the text of a random GUID (version 4), in lower case. Replicas are ordered as their texts.
String newReplicaId([Random? random]) {
  final r = random ?? Random.secure();
  final bytes = List<int>.generate(16, (_) => r.nextInt(256));
  bytes[6] = (bytes[6] & 0x0f) | 0x40;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;
  final hex = bytes.map((b) => b.toRadixString(16).padLeft(2, '0')).join();
  return '${hex.substring(0, 8)}-${hex.substring(8, 12)}-${hex.substring(12, 16)}-${hex.substring(16, 20)}-${hex.substring(20)}';
}

/// A change: its replica and its rank among the changes of that replica (1, 2, 3...).
class Dot {
  const Dot(this.replica, this.seq);
  factory Dot.fromJson(Object? json) {
    final m = _map(json);
    return Dot((m['replica'] as String).toLowerCase(), _int(m['seq']));
  }

  final String replica;
  final int seq;

  Json toJson() => {'replica': replica, 'seq': seq};

  @override
  bool operator ==(Object other) => other is Dot && other.seq == seq && other.replica == replica;

  @override
  int get hashCode => Object.hash(replica, seq);
}

/// A hybrid logical clock value: milliseconds, a counter, and the replica that breaks the remaining ties.
class Hlc implements Comparable<Hlc> {
  const Hlc(this.wall, this.counter, this.replica);
  factory Hlc.fromJson(Object? json) {
    final m = _map(json);
    return Hlc(_int(m['wall']), _int(m['counter']), (m['replica'] as String).toLowerCase());
  }

  final int wall;
  final int counter;
  final String replica;

  Json toJson() => {'wall': wall, 'counter': counter, 'replica': replica};

  @override
  int compareTo(Hlc other) => wall != other.wall
      ? wall.compareTo(other.wall)
      : counter != other.counter
          ? counter.compareTo(other.counter)
          : replica.compareTo(other.replica);
}

/// The stamp of one operation: the stamp of its change, then its rank in the change.
class OpStamp implements Comparable<OpStamp> {
  const OpStamp(this.stamp, this.index);
  factory OpStamp.fromJson(Object? json) {
    final m = _map(json);
    return OpStamp(Hlc.fromJson(m['stamp']), _int(m['index']));
  }

  final Hlc stamp;
  final int index;

  Json toJson() => {'stamp': stamp.toJson(), 'index': index};

  @override
  int compareTo(OpStamp other) {
    final order = stamp.compareTo(other.stamp);
    return order != 0 ? order : index.compareTo(other.index);
  }
}

/// The largest counter of a stamp: an int of .NET. When it is used up, the clock moves to the next millisecond rather than wrap around.
const int _maxStampCounter = 2147483647;

/// The largest value of a counter, and of an increment, either way (2^53 − 1): what a number of JavaScript holds exactly.
const int remoteMaxCounter = 9007199254740991;

Hlc _after(int wall, int counter, String replica) => counter >= _maxStampCounter ? Hlc(wall + 1, 0, replica) : Hlc(wall, counter + 1, replica);

/// A hybrid logical clock (Kulkarni et al.): never goes back, and moves past every value it observes.
class HybridClock {
  HybridClock(this.replica, {int Function()? now, Hlc? last})
      : _now = now ?? (() => DateTime.now().millisecondsSinceEpoch),
        _last = last ?? Hlc(0, 0, replica);

  final String replica;
  final int Function() _now;
  Hlc _last;

  Hlc get last => _last;

  Hlc next() {
    final wall = max(_now(), _last.wall);
    _last = wall == _last.wall ? _after(wall, _last.counter, replica) : Hlc(wall, 0, replica);
    return _last;
  }

  /// A value of another replica: the next values come after it. A counter out of range is never sent by a replica: not followed.
  void observe(Hlc remote) {
    if (remote.counter < 0 || remote.counter > _maxStampCounter) return;
    final last = _last;
    final wall = max(_now(), max(last.wall, remote.wall));
    if (wall != last.wall && wall != remote.wall) {
      _last = Hlc(wall, 0, replica);
      return;
    }
    final counter = wall == last.wall && wall == remote.wall
        ? max(last.counter, remote.counter)
        : wall == last.wall
            ? last.counter
            : remote.counter;
    _last = _after(wall, counter, replica);
  }

  /// Falls back from a clock that ran ahead to the later of now and [floor] (the server's): only after a clock-skew refusal.
  void fallBack(Hlc floor) {
    final wall = max(_now(), floor.wall);
    if (wall < _last.wall) _last = Hlc(wall, wall == floor.wall ? floor.counter : 0, replica);
  }
}

// --------------------------------------------------------------------------------------------------------------------- changes

class DocumentId {
  const DocumentId(this.collection, this.key);
  factory DocumentId.fromJson(Object? json) {
    final m = _map(json);
    return DocumentId(m['collection'] as String, m['key'] as String);
  }

  final String collection;
  final String key;

  Json toJson() => {'collection': collection, 'key': key};

  @override
  bool operator ==(Object other) => other is DocumentId && other.collection == collection && other.key == key;

  @override
  int get hashCode => Object.hash(collection, key);

  @override
  String toString() => '$collection/$key';
}

class ElementTag {
  const ElementTag(this.dot, this.index);
  factory ElementTag.fromJson(Object? json) {
    final m = _map(json);
    return ElementTag(Dot.fromJson(m['dot']), _int(m['index']));
  }

  final Dot dot;
  final int index;

  Json toJson() => {'dot': dot.toJson(), 'index': index};

  @override
  bool operator ==(Object other) => other is ElementTag && other.index == index && other.dot == dot;

  @override
  int get hashCode => Object.hash(dot, index);
}

/// One character of a text field: the insertion that made it, and its rank in the inserted text (UTF-16 code units).
class TextId {
  const TextId(this.dot, this.index, this.offset);
  factory TextId.fromJson(Object? json) {
    final m = _map(json);
    return TextId(Dot.fromJson(m['dot']), _int(m['index']), _int(m['offset']));
  }

  final Dot dot;
  final int index;
  final int offset;

  Json toJson() => {'dot': dot.toJson(), 'index': index, 'offset': offset};

  @override
  bool operator ==(Object other) => other is TextId && other.offset == offset && other.index == index && other.dot == dot;

  @override
  int get hashCode => Object.hash(dot, index, offset);
}

class CharacterSpan {
  const CharacterSpan(this.dot, this.index, this.offset, this.length);
  factory CharacterSpan.fromJson(Object? json) {
    final m = _map(json);
    return CharacterSpan(Dot.fromJson(m['dot']), _int(m['index']), _int(m['offset']), _int(m['length']));
  }

  final Dot dot;
  final int index;
  final int offset;
  final int length;

  Json toJson() => {'dot': dot.toJson(), 'index': index, 'offset': offset, 'length': length};
}

/// One operation on one field of a document. Values and elements are opaque text (JSON by convention).
sealed class FieldOp {
  const FieldOp();

  factory FieldOp.fromJson(Object? json) {
    final m = _map(json);
    return switch (m['op']) {
      'set' => SetField(m['field'] as String, m['value'] as String?),
      'add' => AddElement(m['field'] as String, m['element'] as String),
      'remove' => RemoveElement(m['field'] as String, m['element'] as String, [for (final t in _list(m['observed'])) ElementTag.fromJson(t)]),
      'increment' => IncrementCounter(m['field'] as String, _int(m['delta'])),
      'delete' => const DeleteDocument(),
      'restore' => const RestoreDocument(),
      'insert' => InsertText(m['field'] as String, m['after'] == null ? null : TextId.fromJson(m['after']), m['text'] as String),
      'erase' => EraseText(m['field'] as String, [for (final s in _list(m['spans'])) CharacterSpan.fromJson(s)]),
      final other => throw FormatException('Unknown operation $other.'),
    };
  }

  Json toJson();
}

class SetField extends FieldOp {
  const SetField(this.field, this.value);
  final String field;
  final String? value;
  @override
  Json toJson() => {'op': 'set', 'field': field, 'value': value};
}

class AddElement extends FieldOp {
  const AddElement(this.field, this.element);
  final String field;
  final String element;
  @override
  Json toJson() => {'op': 'add', 'field': field, 'element': element};
}

class RemoveElement extends FieldOp {
  const RemoveElement(this.field, this.element, this.observed);
  final String field;
  final String element;
  final List<ElementTag> observed;
  @override
  Json toJson() => {'op': 'remove', 'field': field, 'element': element, 'observed': [for (final t in observed) t.toJson()]};
}

class IncrementCounter extends FieldOp {
  const IncrementCounter(this.field, this.delta);
  final String field;
  final int delta;
  @override
  Json toJson() => {'op': 'increment', 'field': field, 'delta': delta};
}

class DeleteDocument extends FieldOp {
  const DeleteDocument();
  @override
  Json toJson() => {'op': 'delete'};
}

class RestoreDocument extends FieldOp {
  const RestoreDocument();
  @override
  Json toJson() => {'op': 'restore'};
}

class InsertText extends FieldOp {
  const InsertText(this.field, this.after, this.text);
  final String field;
  final TextId? after;
  final String text;
  @override
  Json toJson() => {'op': 'insert', 'field': field, 'after': after?.toJson(), 'text': text};
}

class EraseText extends FieldOp {
  const EraseText(this.field, this.spans);
  final String field;
  final List<CharacterSpan> spans;
  @override
  Json toJson() => {'op': 'erase', 'field': field, 'spans': [for (final s in spans) s.toJson()]};
}

class DocumentOp {
  const DocumentOp(this.document, this.op);
  factory DocumentOp.fromJson(Object? json) {
    final m = _map(json);
    return DocumentOp(DocumentId.fromJson(m['document']), FieldOp.fromJson(m['op']));
  }

  final DocumentId document;
  final FieldOp op;

  Json toJson() => {'document': document.toJson(), 'op': op.toJson()};
}

/// What one replica did at once: admitted whole or refused whole.
class Change {
  const Change(this.dot, this.stamp, this.ops);
  factory Change.fromJson(Object? json) {
    final m = _map(json);
    return Change(Dot.fromJson(m['dot']), Hlc.fromJson(m['stamp']), [for (final o in _list(m['ops'])) DocumentOp.fromJson(o)]);
  }

  final Dot dot;
  final Hlc stamp;
  final List<DocumentOp> ops;

  Json toJson() => {'dot': dot.toJson(), 'stamp': stamp.toJson(), 'ops': [for (final o in ops) o.toJson()]};
}

/// The four calls of a replica (`Hello`, `Push`, `Pull`, `Snapshot`), as JSON: over SignalR ([SignalRReplicationChannel]) or in a test.
abstract interface class ReplicationChannel {
  Future<Json> call(String method, Json argument);
}

// ------------------------------------------------------------------------------------------------------------------- documents

class _TextNode {
  _TextNode(this.id, this.value, this.stamp, this.erased);
  final TextId id;
  final String value;
  final OpStamp stamp;
  bool erased;
}

class _Register {
  const _Register(this.value, this.stamp);
  final String? value;
  final OpStamp stamp;
}

/// The merged state of one document: what the application reads, and what later operations merge into.
class DocumentState {
  DocumentState(this.id);

  final DocumentId id;
  bool exists = false;
  OpStamp? existenceStamp;
  final Map<String, _Register> _registers = {};
  final Map<String, Map<String, Set<ElementTag>>> _sets = {};
  final Map<String, int> _counters = {};
  final Map<String, List<_TextNode>> _texts = {};

  Iterable<String> get registerNames => _registers.keys;

  /// The sets with at least one element.
  Iterable<String> get setNames => _sets.entries.where((s) => s.value.values.any((tags) => tags.isNotEmpty)).map((s) => s.key);
  Iterable<String> get counterNames => _counters.keys;
  Iterable<String> get textNames => _texts.keys;

  /// A last-writer-wins field: its value (JSON text by convention), null when never set or cleared.
  String? get(String field) => _registers[field]?.value;

  /// The elements of a set, in ordinal order.
  List<String> elements(String field) =>
      [for (final e in (_sets[field] ?? const <String, Set<ElementTag>>{}).entries) if (e.value.isNotEmpty) e.key]..sort();

  List<ElementTag> tagsOf(String field, String element) => [...?_sets[field]?[element]];

  int counter(String field) => _counters[field] ?? 0;

  /// The text of a text field: its characters not erased, in order.
  String text(String field) => [for (final n in _texts[field] ?? const <_TextNode>[]) if (!n.erased) n.value].join();

  /// The characters of a text field not erased, in order: what an edit refers to.
  List<TextId> textIds(String field) => [for (final n in _texts[field] ?? const <_TextNode>[]) if (!n.erased) n.id];

  void apply(FieldOp op, OpStamp stamp, Dot dot) {
    switch (op) {
      case SetField(:final field, :final value):
        final register = _registers[field];
        if (register == null || stamp.compareTo(register.stamp) > 0) _registers[field] = _Register(value, stamp);
      case AddElement(:final field, :final element):
        _tags(field, element).add(ElementTag(dot, stamp.index));
      case RemoveElement(:final field, :final element, :final observed):
        _sets[field]?[element]?.removeAll(observed);
      case IncrementCounter(:final field, :final delta):
        _counters[field] = counter(field) + delta;
      case InsertText(:final field, :final after, :final text):
        _insert(field, after, text, stamp, dot);
      case EraseText(:final field, :final spans):
        for (final node in _texts[field] ?? const <_TextNode>[]) {
          final id = node.id;
          if (spans.any((s) => s.dot == id.dot && s.index == id.index && id.offset >= s.offset && id.offset < s.offset + s.length)) {
            node.erased = true;
          }
        }
      case DeleteDocument() || RestoreDocument():
        final current = existenceStamp;
        if (current == null || stamp.compareTo(current) > 0) {
          exists = op is RestoreDocument;
          existenceStamp = stamp;
        }
    }
  }

  // RGA: right after the anchor, past the characters inserted there by later insertions; a missing anchor (its insertion was
  // refused, the same everywhere) means the start.
  void _insert(String field, TextId? after, String text, OpStamp stamp, Dot dot) {
    if (text.isEmpty) return;
    final nodes = _texts.putIfAbsent(field, () => []);
    final first = TextId(dot, stamp.index, 0);
    if (nodes.any((n) => n.id == first)) return;
    var at = 0;
    if (after != null) {
      final anchor = nodes.indexWhere((n) => n.id == after);
      at = anchor < 0 ? 0 : anchor + 1;
    }
    while (at < nodes.length) {
      final order = nodes[at].stamp.compareTo(stamp);
      if (order > 0 || (order == 0 && nodes[at].id.offset > 0)) {
        at++;
      } else {
        break;
      }
    }
    nodes.insertAll(at, [for (var offset = 0; offset < text.length; offset++) _TextNode(TextId(dot, stamp.index, offset), text[offset], stamp, false)]);
  }

  Set<ElementTag> _tags(String field, String element) => _sets.putIfAbsent(field, () => {}).putIfAbsent(element, () => <ElementTag>{});

  DocumentState clone() => DocumentState.fromSnapshot(toSnapshot());

  /// The state as the JSON of a `DocumentSnapshot`, as the server sends it in a snapshot.
  Json toSnapshot() => {
        'id': id.toJson(),
        'exists': exists,
        'existenceStamp': existenceStamp?.toJson(),
        'registers': [for (final r in _registers.entries) {'field': r.key, 'value': r.value.value, 'stamp': r.value.stamp.toJson()}],
        'elements': [
          for (final s in _sets.entries)
            for (final e in s.value.entries)
              if (e.value.isNotEmpty) {'field': s.key, 'element': e.key, 'tags': [for (final t in e.value) t.toJson()]},
        ],
        'counters': [for (final c in _counters.entries) {'field': c.key, 'value': c.value}],
        'texts': [for (final t in _texts.entries) {'field': t.key, 'runs': _runs(t.value)}],
      };

  static List<Json> _runs(List<_TextNode> nodes) {
    final runs = <Json>[];
    var i = 0;
    while (i < nodes.length) {
      final start = nodes[i];
      final text = StringBuffer(start.value);
      var j = i + 1;
      while (j < nodes.length &&
          nodes[j].id.dot == start.id.dot &&
          nodes[j].id.index == start.id.index &&
          nodes[j].id.offset == start.id.offset + (j - i) &&
          nodes[j].erased == start.erased) {
        text.write(nodes[j++].value);
      }
      runs.add({'start': start.id.toJson(), 'stamp': start.stamp.toJson(), 'text': text.toString(), 'erased': start.erased});
      i = j;
    }
    return runs;
  }

  static DocumentState fromSnapshot(Object? json) {
    final m = _map(json);
    final state = DocumentState(DocumentId.fromJson(m['id']))
      ..exists = m['exists'] as bool
      ..existenceStamp = m['existenceStamp'] == null ? null : OpStamp.fromJson(m['existenceStamp']);
    for (final r in _list(m['registers'])) {
      final x = _map(r);
      state._registers[x['field'] as String] = _Register(x['value'] as String?, OpStamp.fromJson(x['stamp']));
    }
    for (final e in _list(m['elements'])) {
      final x = _map(e);
      state._tags(x['field'] as String, x['element'] as String).addAll([for (final t in _list(x['tags'])) ElementTag.fromJson(t)]);
    }
    for (final c in _list(m['counters'])) {
      final x = _map(c);
      state._counters[x['field'] as String] = _int(x['value']);
    }
    for (final t in _list(m['texts'])) {
      final x = _map(t);
      final nodes = state._texts[x['field'] as String] = [];
      for (final run in _list(x['runs'])) {
        final r = _map(run);
        final start = TextId.fromJson(r['start']);
        final stamp = OpStamp.fromJson(r['stamp']);
        final text = r['text'] as String;
        for (var i = 0; i < text.length; i++) {
          nodes.add(_TextNode(TextId(start.dot, start.index, start.offset + i), text[i], stamp, r['erased'] as bool));
        }
      }
    }
    return state;
  }
}

/// Documents and the changes applied to them, each once (the last seq applied per replica).
class DocumentSet {
  final Map<DocumentId, DocumentState> _documents = {};
  final Map<String, int> _applied = {};

  Iterable<DocumentState> get all => _documents.values;

  DocumentState? find(DocumentId id) => _documents[id];

  bool hasApplied(Dot dot) => dot.seq <= (_applied[dot.replica] ?? 0);

  /// Applies a change; false when it was already applied.
  bool apply(Change change) {
    if (hasApplied(change.dot)) return false;
    _applied[change.dot.replica] = change.dot.seq;
    for (var index = 0; index < change.ops.length; index++) {
      final op = change.ops[index];
      _documents.putIfAbsent(op.document, () => DocumentState(op.document)).apply(op.op, OpStamp(change.stamp, index), change.dot);
    }
    return true;
  }

  void replace(List<Object?> snapshots, Set<String> collections) {
    _documents.removeWhere((id, _) => collections.contains(id.collection));
    for (final s in snapshots) {
      final state = DocumentState.fromSnapshot(s);
      _documents[state.id] = state;
    }
    _applied.clear();
  }

  void load(List<Object?> snapshots, List<Object?> applied) {
    for (final s in snapshots) {
      final state = DocumentState.fromSnapshot(s);
      _documents[state.id] = state;
    }
    for (final d in applied) {
      final dot = Dot.fromJson(d);
      _applied[dot.replica] = max(dot.seq, _applied[dot.replica] ?? 0);
    }
  }

  List<Dot> get appliedDots => [for (final a in _applied.entries) Dot(a.key, a.value)];
}

// ------------------------------------------------------------------------------------------------------------------------ replica

/// A change the server refused: its effect is gone; kept for the application to show, correct or drop.
class RejectedChange {
  const RejectedChange(this.change, this.code, this.message);
  factory RejectedChange.fromJson(Object? json) {
    final m = _map(json);
    return RejectedChange(Change.fromJson(m['change']), m['code'] as String?, m['message'] as String?);
  }

  final Change change;
  final String? code;
  final String? message;

  Json toJson() => {'change': change.toJson(), 'code': code, 'message': message};
}

/// Where a replica keeps its state between two runs: one text, written whole (a file, shared preferences, IndexedDB...).
abstract interface class ReplicaStorage {
  Future<String?> read();
  Future<void> write(String state);
}

class MemoryReplicaStorage implements ReplicaStorage {
  String? _state;
  @override
  Future<String?> read() async => _state;
  @override
  Future<void> write(String state) async => _state = state;
}

class SyncReport {
  const SyncReport(this.pushed, this.rejected, this.pulled, this.snapshot);
  final int pushed;
  final int rejected;
  final int pulled;
  final bool snapshot;
}

class _Pending {
  _Pending(this.change, this.position);
  Change change;
  int position;
}

/// Accepted, but its position is not known (a duplicate whose answer the server forgot).
const int _unknown = 9007199254740991;

typedef _View = DocumentState? Function(DocumentId id);

/// A replica that writes offline at once and converges with the server when it synchronizes. What the application reads is
/// the confirmed documents (from pulls) with the pending changes of this replica over them: a refused change leaves exactly.
class Replica {
  Replica._(this.id, this._storage, this.clock);

  final String id;
  final HybridClock clock;
  final ReplicaStorage _storage;
  final DocumentSet _confirmed = DocumentSet();
  List<_Pending> _pending = [];
  List<RejectedChange> _rejected = [];
  final Set<String> _collections = {};
  int _nextSeq = 1;
  int _cursor = 0;
  final StreamController<List<DocumentId>> _changed = StreamController.broadcast();
  Future<void> _queue = Future.value();

  /// Opens the replica stored in [storage], or a new one; other collections than those stored ask a snapshot.
  static Future<Replica> open(ReplicaStorage storage, List<String> collections, {int Function()? now}) async {
    final text = await storage.read();
    final Replica replica;
    if (text == null) {
      final id = newReplicaId();
      replica = Replica._(id, storage, HybridClock(id, now: now));
    } else {
      final m = _map(jsonDecode(text));
      final id = m['id'] as String;
      replica = Replica._(id, storage, HybridClock(id, now: now, last: Hlc.fromJson(m['clock'])));
      replica._confirmed.load(_list(m['documents']), _list(m['applied']));
      replica._pending = [for (final p in _list(m['pending'])) _Pending(Change.fromJson(_map(p)['change']), _int(_map(p)['position']))];
      replica._rejected = [for (final r in _list(m['rejected'])) RejectedChange.fromJson(r)];
      replica._nextSeq = _int(m['nextSeq']);
      final stored = _list(m['collections']).cast<String>().toSet();
      replica._cursor = stored.length == collections.length && stored.containsAll(collections) ? _int(m['cursor']) : -1;
    }
    replica._collections.addAll(collections);
    await replica._save();
    return replica;
  }

  int get cursor => _cursor;
  int get pendingCount => _pending.length;
  List<RejectedChange> get rejected => List.unmodifiable(_rejected);
  List<String> get collections => [..._collections];

  /// The documents that may have changed, after local changes and after a synchronization.
  Stream<List<DocumentId>> get changed => _changed.stream;

  /// The document as the application sees it.
  DocumentState? find(DocumentId id) => _view(id);

  /// The documents of a collection, deleted ones excepted.
  List<DocumentState> all(String collection) {
    final ids = <DocumentId>{
      for (final d in _confirmed.all) if (d.id.collection == collection) d.id,
      for (final p in _pending) for (final o in p.change.ops) if (o.document.collection == collection) o.document,
    };
    return [for (final id in ids) if (_view(id) case final d? when d.exists) d];
  }

  DocumentState? _view(DocumentId id) {
    var state = _confirmed.find(id)?.clone();
    for (final p in _pending) {
      for (var index = 0; index < p.change.ops.length; index++) {
        final op = p.change.ops[index];
        if (op.document != id) continue;
        state ??= DocumentState(id);
        state.apply(op.op, OpStamp(p.change.stamp, index), p.change.dot);
      }
    }
    return state;
  }

  /// Starts a change: operations collected, then [ChangeBuilder.commit] stores them as one change.
  ChangeBuilder edit() => ChangeBuilder._(this);

  Future<Change> _commit(List<(DocumentId, List<FieldOp> Function(_View view))> entries) async {
    final dot = Dot(id, _nextSeq);
    final stamp = clock.next();
    final working = <DocumentId, DocumentState?>{};
    DocumentState? current(DocumentId id) => working.containsKey(id) ? working[id] : (working[id] = _view(id));
    final ops = <DocumentOp>[];
    for (final (document, build) in entries) {
      for (final op in build(current)) {
        final state = current(document) ?? (working[document] = DocumentState(document));
        state.apply(op, OpStamp(stamp, ops.length), dot);
        ops.add(DocumentOp(document, op));
      }
    }
    if (ops.isEmpty) throw StateError('The change does nothing.');
    _nextSeq++;
    final change = Change(dot, stamp, ops);
    _pending.add(_Pending(change, 0));
    await _save();
    _changed.add([for (final o in ops) o.document]);
    return change;
  }

  /// Sends the pending changes, then receives what the server has after the cursor (or a snapshot when the cursor is too old).
  /// Stops at the first failure of the channel, with everything stored: call it again. Calls are queued, one at a time.
  Future<SyncReport> sync(ReplicationChannel channel) {
    final run = _queue.then((_) => _syncNow(channel));
    _queue = run.then((_) {}, onError: (_) {});
    return run;
  }

  Future<SyncReport> _syncNow(ReplicationChannel channel) async {
    final welcome = await channel.call('Hello', {'protocol': remoteProtocolVersion, 'replica': id, 'clock': clock.last.toJson()});
    if (welcome['success'] != true) throw StateError(welcome['error'] as String? ?? 'The server refused the replica.');
    final serverClock = Hlc.fromJson(welcome['clock']);
    clock.observe(serverClock);
    final (pushed, refused) = await _push(channel, serverClock, max(1, _int(welcome['maxChangesPerPush'])));
    final (pulled, snapshot) = await _pull(channel);
    return SyncReport(pushed, refused, pulled, snapshot);
  }

  Future<(int, int)> _push(ReplicationChannel channel, Hlc serverClock, int batchSize) async {
    var pushed = 0;
    var refused = 0;
    var fellBack = false;
    while (true) {
      final batch = [for (final p in _pending) if (p.position == 0) p.change].take(batchSize).toList();
      if (batch.isEmpty) return (pushed, refused);
      final result = await channel.call('Push', {'changes': [for (final c in batch) c.toJson()]});
      final acks = _list(result['acks']);
      final restamp = <Change>[];
      final touched = <DocumentId>[];
      for (final a in acks) {
        final ack = _map(a);
        final dot = Dot.fromJson(ack['dot']);
        final index = _pending.indexWhere((p) => p.change.dot == dot);
        if (index < 0) continue;
        pushed++;
        final entry = _pending[index];
        final status = _int(ack['status']);
        if (status == 1 || status == 3) {
          final position = ack['position'] == null ? 0 : _int(ack['position']);
          entry.position = position > 0 ? position : _unknown;
        } else if (ack['code'] == RemoteErrorCodes.clockSkew) {
          restamp.add(entry.change);
          _pending.removeAt(index);
        } else {
          refused++;
          _rejected.add(RejectedChange(entry.change, ack['code'] as String?, ack['message'] as String?));
          _pending.removeAt(index);
          touched.addAll(entry.change.ops.map((o) => o.document));
        }
      }
      if (restamp.isNotEmpty) clock.fallBack(serverClock);
      final renamed = <Dot, Dot>{};
      for (final change in restamp) {
        final dot = Dot(id, _nextSeq++);
        renamed[change.dot] = dot;
        _pending.add(_Pending(Change(dot, clock.next(), change.ops), 0));
      }
      if (renamed.isNotEmpty) {
        for (final p in _pending) {
          p.change = _rename(p.change, renamed);
        }
      }
      await _save();
      if (touched.isNotEmpty) _changed.add(touched);
      if (acks.length < batch.length) return (pushed, refused);
      if (restamp.isNotEmpty) {
        if (fellBack) return (pushed, refused);
        fellBack = true;
      }
    }
  }

  Future<(int, bool)> _pull(ReplicationChannel channel) async {
    var pulled = 0;
    var snapshot = false;
    final touched = <DocumentId>{};
    final collections = this.collections;
    var since = _cursor;
    while (true) {
      final Json result = since < 0
          ? {'entries': const <Object?>[], 'position': since, 'more': false, 'snapshotRequired': true}
          : await channel.call('Pull', {'collections': collections, 'since': since, 'max': 500});

      if (result['snapshotRequired'] == true) {
        final full = await channel.call('Snapshot', {'collections': collections});
        final position = _int(full['position']);
        touched.addAll(_confirmed.all.map((d) => d.id));
        _confirmed.replace(_list(full['documents']), collections.toSet());
        touched.addAll(_confirmed.all.map((d) => d.id));
        _pending.removeWhere((p) => p.position > 0 && p.position <= position);
        _cursor = since = position;
        snapshot = true;
        await _save();
        continue;
      }

      for (final e in _list(result['entries'])) {
        final change = Change.fromJson(_map(e)['change']);
        if (_confirmed.apply(change)) pulled++;
        _pending.removeWhere((p) => p.change.dot == change.dot);
        touched.addAll(change.ops.map((o) => o.document));
      }
      final position = _int(result['position']);
      final more = result['more'] == true;
      _pending.removeWhere((p) => p.position > 0 && p.position <= position);
      _cursor = since = position;
      if (!more) {
        for (final lost in _pending.where((p) => p.position == _unknown)) {
          _rejected.add(RejectedChange(lost.change, RemoteErrorCodes.lost, 'The server decided on this change before, and its answer is lost: it is not in the log.'));
          touched.addAll(lost.change.ops.map((o) => o.document));
        }
        _pending.removeWhere((p) => p.position == _unknown);
      }
      await _save();
      if (!more) break;
    }
    if (touched.isNotEmpty) _changed.add([...touched]);
    return (pulled, snapshot);
  }

  /// Forgets a refused change, once the application dealt with it.
  Future<void> dismiss(RejectedChange rejected) async {
    _rejected.remove(rejected);
    await _save();
  }

  Future<void> _save() => _storage.write(jsonEncode({
        'format': 1,
        'id': id,
        'clock': clock.last.toJson(),
        'nextSeq': _nextSeq,
        'cursor': _cursor,
        'collections': collections,
        'documents': [for (final d in _confirmed.all) d.toSnapshot()],
        'applied': [for (final d in _confirmed.appliedDots) d.toJson()],
        'pending': [for (final p in _pending) {'change': p.change.toJson(), 'position': p.position}],
        'rejected': [for (final r in _rejected) r.toJson()],
      }));
}

Change _rename(Change change, Map<Dot, Dot> renamed) {
  Dot of(Dot dot) => renamed[dot] ?? dot;
  return Change(change.dot, change.stamp, [
    for (final o in change.ops)
      DocumentOp(
        o.document,
        switch (o.op) {
          RemoveElement(:final field, :final element, :final observed) =>
            RemoveElement(field, element, [for (final t in observed) ElementTag(of(t.dot), t.index)]),
          InsertText(:final field, :final after?, :final text) => InsertText(field, TextId(of(after.dot), after.index, after.offset), text),
          EraseText(:final field, :final spans) => EraseText(field, [for (final s in spans) CharacterSpan(of(s.dot), s.index, s.offset, s.length)]),
          final op => op,
        },
      ),
  ]);
}

List<CharacterSpan> _spans(Iterable<TextId> ids) {
  final result = <CharacterSpan>[];
  for (final id in ids) {
    final last = result.isEmpty ? null : result.last;
    if (last != null && last.dot == id.dot && last.index == id.index && last.offset + last.length == id.offset) {
      result[result.length - 1] = CharacterSpan(last.dot, last.index, last.offset, last.length + 1);
    } else {
      result.add(CharacterSpan(id.dot, id.index, id.offset, 1));
    }
  }
  return result;
}

bool _isHigh(int c) => c >= 0xd800 && c <= 0xdbff;
bool _isLow(int c) => c >= 0xdc00 && c <= 0xdfff;

/// The smallest edit from the text of a field to [text]: the common start and end are kept.
List<FieldOp> _diff(DocumentState? state, String field, String text) {
  final current = state?.text(field) ?? '';
  var start = 0;
  while (start < current.length && start < text.length && current.codeUnitAt(start) == text.codeUnitAt(start)) {
    start++;
  }
  if (start > 0 && _isHigh(current.codeUnitAt(start - 1))) start--;
  var end = 0;
  while (end < current.length - start &&
      end < text.length - start &&
      current.codeUnitAt(current.length - 1 - end) == text.codeUnitAt(text.length - 1 - end)) {
    end++;
  }
  if (end > 0 && _isLow(current.codeUnitAt(current.length - end))) end--;
  final ids = state?.textIds(field) ?? const <TextId>[];
  final erased = current.length - start - end;
  final inserted = text.substring(start, text.length - end);
  return [
    if (erased > 0) EraseText(field, _spans(ids.sublist(start, start + erased))),
    if (inserted.isNotEmpty) InsertText(field, start == 0 ? null : ids[start - 1], inserted),
  ];
}

/// The operations of one change, built at commit from what the replica sees, each seeing the ones before it.
class ChangeBuilder {
  ChangeBuilder._(this._replica);

  final Replica _replica;
  final List<(DocumentId, List<FieldOp> Function(_View view))> _entries = [];

  ChangeBuilder _add(DocumentId document, List<FieldOp> Function(_View view) build) {
    _entries.add((document, build));
    return this;
  }

  int get count => _entries.length;

  /// Creates the document (or restores it after a deletion).
  ChangeBuilder create(DocumentId document) => _add(document, (_) => const [RestoreDocument()]);
  ChangeBuilder delete(DocumentId document) => _add(document, (_) => const [DeleteDocument()]);

  /// A last-writer-wins field; the value is opaque text (JSON by convention), null clears it.
  ChangeBuilder set(DocumentId document, String field, String? value) => _add(document, (_) => [SetField(field, value)]);
  ChangeBuilder addElement(DocumentId document, String field, String element) => _add(document, (_) => [AddElement(field, element)]);

  /// Removes the element as this replica sees it: an addition made elsewhere and not seen yet survives.
  ChangeBuilder removeElement(DocumentId document, String field, String element) =>
      _add(document, (view) => [RemoveElement(field, element, view(document)?.tagsOf(field, element) ?? const [])]);
  ChangeBuilder increment(DocumentId document, String field, int delta) {
    if (delta < -remoteMaxCounter || delta > remoteMaxCounter) throw RangeError.range(delta, -remoteMaxCounter, remoteMaxCounter, 'delta');
    return _add(document, (_) => [IncrementCounter(field, delta)]);
  }

  /// Inserts [text] at [position] (UTF-16 code units) of the text this replica sees.
  ChangeBuilder insertText(DocumentId document, String field, int position, String text) => _add(document, (view) {
        final ids = view(document)?.textIds(field) ?? const <TextId>[];
        if (position < 0 || position > ids.length) throw RangeError.range(position, 0, ids.length, 'position');
        return text.isEmpty ? const [] : [InsertText(field, position == 0 ? null : ids[position - 1], text)];
      });

  ChangeBuilder eraseText(DocumentId document, String field, int position, int length) => _add(document, (view) {
        final ids = view(document)?.textIds(field) ?? const <TextId>[];
        if (position < 0 || length < 0 || position + length > ids.length) throw RangeError('The text has ${ids.length} characters.');
        return length == 0 ? const [] : [EraseText(field, _spans(ids.sublist(position, position + length)))];
      });

  /// Gives a text field a new value with the smallest edit, so that what others change elsewhere in it survives.
  ChangeBuilder replaceText(DocumentId document, String field, String text) => _add(document, (view) => _diff(view(document), field, text));

  Future<Change> commit() {
    if (_entries.isEmpty) return Future.error(StateError('A change has at least one operation.'));
    return _replica._commit(_entries);
  }
}
