import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:routeables_client/routeables_client.dart';

/// What a document shows, as the .NET fixture writes it.
Map<String, Object?> fingerprint(DocumentState state) {
  Map<String, Object?> sorted(Iterable<String> names, Object? Function(String name) value) =>
      {for (final name in [...names]..sort()) name: value(name)};
  return {
    'collection': state.id.collection,
    'key': state.id.key,
    'exists': state.exists,
    'registers': sorted(state.registerNames, state.get),
    'elements': sorted(state.setNames, state.elements),
    'counters': sorted(state.counterNames, state.counter),
    'texts': sorted(state.textNames, state.text),
  };
}

int byId(DocumentState a, DocumentState b) =>
    a.id.collection != b.id.collection ? a.id.collection.compareTo(b.id.collection) : a.id.key.compareTo(b.id.key);

/// The rules of the reference server, in memory and as JSON, for what the client does (the real server: tests/e2e).
class TestServer implements ReplicationChannel {
  final List<Map<String, Object?>> log = [];
  final DocumentSet documents = DocumentSet();
  final Map<String, (int, List<Map<String, Object?>>)> replicas = {};
  int horizon = 0;
  bool down = false;
  bool losePushAnswer = false;
  String? Function(Change change) refuse = (_) => null;

  int get position => log.isEmpty ? horizon : log.last['position'] as int;

  @override
  Future<Map<String, Object?>> call(String method, Map<String, Object?> argument) async {
    if (down) throw StateError('No network.');
    final request = jsonDecode(jsonEncode(argument)) as Map<String, Object?>;
    final Map<String, Object?> answer = switch (method) {
      'Hello' => {
          'success': request['protocol'] == remoteProtocolVersion,
          'protocol': remoteProtocolVersion,
          'clock': request['clock'],
          'lastSeq': replicas[request['replica']]?.$1 ?? 0,
          'maxChangesPerPush': 3,
        },
      'Push' => {'acks': [for (final c in request['changes'] as List) _admit(Change.fromJson(c))]},
      'Pull' => _pull(request),
      'Snapshot' => {'documents': [for (final d in documents.all) d.toSnapshot()], 'position': position},
      _ => throw ArgumentError(method),
    };
    if (method == 'Push' && losePushAnswer) {
      losePushAnswer = false;
      throw StateError('Reset.');
    }
    return jsonDecode(jsonEncode(answer)) as Map<String, Object?>;
  }

  Map<String, Object?> _admit(Change change) {
    final (lastSeq, acks) = replicas[change.dot.replica] ?? (0, <Map<String, Object?>>[]);
    if (change.dot.seq <= lastSeq) {
      final known = acks.firstWhere((a) => (a['dot'] as Map)['seq'] == change.dot.seq);
      return {...known, 'status': known['status'] == 1 ? 3 : known['status']};
    }
    final code = refuse(change);
    final Map<String, Object?> ack;
    if (code != null) {
      ack = {'dot': change.dot.toJson(), 'status': 2, 'position': 0, 'code': code, 'message': 'no'};
    } else {
      final entry = {'position': position + 1, 'change': change.toJson()};
      log.add(entry);
      documents.apply(change);
      ack = {'dot': change.dot.toJson(), 'status': 1, 'position': entry['position']};
    }
    replicas[change.dot.replica] = (change.dot.seq, [...acks, ack]);
    return ack;
  }

  Map<String, Object?> _pull(Map<String, Object?> request) {
    final since = request['since'] as int;
    if (since < horizon) return {'entries': const <Object?>[], 'position': since, 'more': false, 'snapshotRequired': true};
    final page = log.where((e) => (e['position'] as int) > since).take(2).toList();
    final at = page.length == 2 ? page.last['position'] as int : position;
    return {'entries': page, 'position': at, 'more': at < position, 'snapshotRequired': false};
  }
}

const ana = DocumentId('Person', 'ana');
const note = DocumentId('Note', 'n1');

void main() {
  group('the shared fixture (written by the .NET implementation)', () {
    final fixture = jsonDecode(File('../fixtures/remote-v1.json').readAsStringSync()) as Map<String, Object?>;

    test('merges the log into what .NET shows', () {
      expect(fixture['protocol'], remoteProtocolVersion);
      final set = DocumentSet();
      for (final entry in (fixture['log'] as Map)['entries'] as List) {
        set.apply(Change.fromJson((entry as Map)['change']));
      }
      expect([for (final d in [...set.all]..sort(byId)) fingerprint(d)], fixture['expected']);
    });

    test('reads the snapshot into what .NET shows', () {
      final states = [for (final d in (fixture['snapshot'] as Map)['documents'] as List) DocumentState.fromSnapshot(d)]..sort(byId);
      expect([for (final d in states) fingerprint(d)], fixture['expected']);
      expect([for (final d in states) fingerprint(DocumentState.fromSnapshot(d.toSnapshot()))], fixture['expected']);
    });
  });

  group('a replica', () {
    test('writes offline at once, and two devices converge', () async {
      final server = TestServer();
      final a = await Replica.open(MemoryReplicaStorage(), ['Person', 'Note']);
      final b = await Replica.open(MemoryReplicaStorage(), ['Person', 'Note']);
      await a.edit().create(ana).addElement(ana, 'tags', '"x"').create(note).insertText(note, 'body', 0, 'The quick fox').commit();
      await a.sync(server);
      await b.sync(server);

      await a.edit().removeElement(ana, 'tags', '"x"').increment(ana, 'visits', 2).replaceText(note, 'body', 'The quick brown fox').commit();
      await b.edit().addElement(ana, 'tags', '"x"').increment(ana, 'visits', 3).replaceText(note, 'body', 'The quick fox jumps').commit();
      expect(a.find(ana)!.elements('tags'), isEmpty);
      for (final r in [a, b, a]) {
        await r.sync(server);
      }

      for (final r in [a, b]) {
        expect(r.find(ana)!.elements('tags'), ['"x"']);
        expect(r.find(ana)!.counter('visits'), 5);
        expect(r.find(note)!.text('body'), 'The quick brown fox jumps');
        expect(r.pendingCount, 0);
      }
    });

    test('is not applied twice when an answer is lost, and survives a restart', () async {
      final server = TestServer();
      final storage = MemoryReplicaStorage();
      final a = await Replica.open(storage, ['Person']);
      await a.edit().create(ana).increment(ana, 'visits', 1).commit();
      server.losePushAnswer = true;
      await expectLater(a.sync(server), throwsStateError);
      final reopened = await Replica.open(storage, ['Person']);
      await reopened.sync(server);
      expect(server.log.length, 1);
      expect(reopened.find(ana)!.counter('visits'), 1);
      expect(reopened.pendingCount, 0);
    });

    test('drops a refused change whole, and keeps it for the application', () async {
      final server = TestServer()..refuse = (c) => c.ops.any((o) => o.op is SetField && (o.op as SetField).value == '"forbidden"') ? 'invalid' : null;
      final a = await Replica.open(MemoryReplicaStorage(), ['Person']);
      await a.edit().create(ana).set(ana, 'name', '"Ana"').commit();
      await a.edit().set(ana, 'name', '"forbidden"').increment(ana, 'visits', 1).commit();
      final report = await a.sync(server);
      expect(report.rejected, 1);
      expect(a.find(ana)!.get('name'), '"Ana"');
      expect(a.find(ana)!.counter('visits'), 0);
      expect(a.rejected.single.code, 'invalid');
    });

    test('takes a snapshot below the horizon and counts its own change once', () async {
      final server = TestServer();
      final a = await Replica.open(MemoryReplicaStorage(), ['Person']);
      final b = await Replica.open(MemoryReplicaStorage(), ['Person']);
      await b.edit().create(ana).increment(ana, 'visits', 10).commit();
      await b.sync(server);
      await a.edit().increment(ana, 'visits', 1).commit();
      await a.sync(server);
      for (var i = 0; i < 3; i++) {
        await b.edit().increment(ana, 'visits', 1).commit();
        await b.sync(server);
      }
      server
        ..horizon = server.position
        ..log.clear();
      await a.edit().increment(ana, 'visits', 1).commit();
      final report = await a.sync(server);
      expect(report.snapshot, isTrue);
      expect(a.find(ana)!.counter('visits'), 15);
      expect(a.pendingCount, 0);
    });
  });
}
