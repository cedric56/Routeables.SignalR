import 'dart:async';

import 'package:flutter_test/flutter_test.dart';
import 'package:routeables_client/routeables_client.dart';

import 'client_test.dart' show FakeTransport, tick;

/// Records what the bridge sends.
class Recorder {
  final List<(String, List<Object?>)> calls = [];

  Future<Object?> call(String method, List<Object?> args) async {
    calls.add((method, args));
    return {'success': true};
  }
}

/// Preferences in memory: the shape of the browser's and of the Flutter sample's (`{container: {key: value}}`).
EssentialsService memoryPreferences(Map<String, Map<String, String>> containers) => EssentialsService(
      state: () => {for (final MapEntry(:key, :value) in containers.entries) key: {...value}},
      methods: {
        'set': (args) {
          final [container, key, value] = args;
          if (container is! String || key is! String) throw ArgumentError('The container and the key must be strings.');
          (containers[container] ??= {})[key] = '$value';
          return null;
        },
      },
    );

void main() {
  group('the Essentials bridge', () {
    test('announces its services and their state', () async {
      final recorder = Recorder();
      final bridge = EssentialsBridge({
        'preferences': memoryPreferences({
          '': {'theme': 'dark'},
          'settings': {'volume': '7'},
        }),
        'vibration': EssentialsService(methods: {'vibrate': (_) => null}),
      }, recorder.call);

      await bridge.ready();

      expect(recorder.calls.single.$1, 'EssentialsReady');
      expect(recorder.calls.single.$2.single, {
        'version': essentialsProtocol,
        'capabilities': ['preferences', 'vibration'],
        'state': {
          'preferences': {
            '': {'theme': 'dark'},
            'settings': {'volume': '7'},
          },
        },
      });
    });

    test('announces a service whose state fails without its state', () async {
      final recorder = Recorder();
      final bridge = EssentialsBridge({
        'battery': EssentialsService(state: () async => throw StateError('no battery')),
      }, recorder.call);

      final announcement = await bridge.announcement();

      expect(announcement['capabilities'], ['battery']);
      expect(announcement['state'], isEmpty);
    });

    test('runs a method the service lists and answers with its result', () async {
      final recorder = Recorder();
      final containers = <String, Map<String, String>>{};
      final bridge = EssentialsBridge({'preferences': memoryPreferences(containers)}, recorder.call);

      await bridge.invoke({
        'id': 'e1',
        'service': 'preferences',
        'method': 'set',
        'args': ['', 'theme', 'light'],
      });

      expect(containers['']!['theme'], 'light');
      expect(recorder.calls.single.$1, 'EssentialsResult');
      expect(recorder.calls.single.$2.single, {'id': 'e1', 'success': true, 'result': null});
    });

    test('never runs what the service does not list', () async {
      final recorder = Recorder();
      final bridge = EssentialsBridge({'preferences': memoryPreferences({})}, recorder.call);

      for (final (service, method) in [('preferences', 'toString'), ('preferences', 'hashCode'), ('geolocation', 'getLocation'), (42, 'set')]) {
        await bridge.invoke({'id': '$service.$method', 'service': service, 'method': method, 'args': []});
      }

      expect(recorder.calls, hasLength(4));
      for (final (_, args) in recorder.calls) {
        final answer = args.single! as Map<String, Object?>;
        expect(answer['success'], false);
        expect(answer['code'], 'not-supported');
      }
    });

    test('reports the failures with the codes of the server', () async {
      final recorder = Recorder();
      final bridge = EssentialsBridge({
        'camera': EssentialsService(methods: {
          'denied': (_) => throw const EssentialsError('no', EssentialsErrorCodes.permissionDenied),
          'rejected': (_) => Future.error(const EssentialsError('off', EssentialsErrorCodes.notEnabled)),
          'slow': (_) => Future.error(TimeoutException('late')),
          'missing': (_) => throw UnimplementedError(),
        }),
        'preferences': memoryPreferences({}),
      }, recorder.call);

      for (final method in ['denied', 'rejected', 'slow', 'missing']) {
        await bridge.invoke({'id': method, 'service': 'camera', 'method': method, 'args': []});
      }
      await bridge.invoke({
        'id': 'p',
        'service': 'preferences',
        'method': 'set',
        'args': ['', 42, 'x'],
      });

      expect([for (final (_, args) in recorder.calls) (args.single! as Map)['code']],
          ['permission-denied', 'not-enabled', 'timeout', 'not-supported', 'invalid-argument']);
      expect((recorder.calls.first.$2.single! as Map)['error'], 'no');
      expect(essentialsErrorCode(Exception('plain')), 'failed');
    });

    test('starts and stops the events the server asks for, and only those', () async {
      final recorder = Recorder();
      void Function(Object?)? emit;
      var stopped = 0;
      final bridge = EssentialsBridge({
        'connectivity': EssentialsService(events: {
          'changed': (e) {
            emit = e;
            return () => stopped++;
          },
        }),
      }, recorder.call);

      bridge.subscribe({'service': 'connectivity', 'event': 'unknown', 'active': true});
      bridge.subscribe({'service': 'connectivity', 'event': 'changed', 'active': true});
      bridge.subscribe({'service': 'connectivity', 'event': 'changed', 'active': true}); // asked again: one listener
      emit!({'networkAccess': 'None', 'profiles': <String>[]});
      await tick();
      bridge.subscribe({'service': 'connectivity', 'event': 'changed', 'active': false});

      expect(recorder.calls.single.$1, 'EssentialsEvent');
      expect(recorder.calls.single.$2.single, {
        'service': 'connectivity',
        'event': 'changed',
        'payload': {'networkAccess': 'None', 'profiles': <String>[]},
      });
      expect(stopped, 1);
    });

    test('stops its events when it announces itself again: the server asks for the ones it still wants', () async {
      final recorder = Recorder();
      var stopped = 0;
      final bridge = EssentialsBridge({
        'connectivity': EssentialsService(events: {'changed': (_) => () => stopped++}),
      }, recorder.call);
      bridge.subscribe({'service': 'connectivity', 'event': 'changed', 'active': true});

      await bridge.ready();

      expect(stopped, 1);
    });
  });

  group('a client with Essentials', () {
    test('announces them right after each Hello, before it is connected', () async {
      final transport = FakeTransport();
      final client = RouteablesClient(transport, essentials: {'preferences': memoryPreferences({})});
      final seen = <bool>[];
      client.onConnection(seen.add);

      await client.connect();
      expect([for (final (method, _) in transport.calls) method], ['Hello', 'EssentialsReady']);
      expect(seen, [true]);

      transport.changeConnection(false);
      transport.changeConnection(true);
      await tick();
      await tick();
      expect([for (final (method, _) in transport.calls) method], ['Hello', 'EssentialsReady', 'Hello', 'EssentialsReady']);
    });

    test('answers the calls of the server', () async {
      final transport = FakeTransport();
      final containers = <String, Map<String, String>>{};
      final client = RouteablesClient(transport, essentials: {'preferences': memoryPreferences(containers)});
      await client.connect();

      transport.server.essentialsInvoke!({
        'id': 'e1',
        'service': 'preferences',
        'method': 'set',
        'args': ['', 'k', 'v'],
      });
      await tick();

      expect(containers['']!['k'], 'v');
      expect(transport.calls.last.$1, 'EssentialsResult');
      expect(transport.calls.last.$2.single, {'id': 'e1', 'success': true, 'result': null});
    });

    test('says nothing of Essentials without them', () async {
      final transport = FakeTransport();
      final client = RouteablesClient(transport);
      await client.connect();

      expect([for (final (method, _) in transport.calls) method], ['Hello']);
      expect(transport.server.essentialsInvoke, isNull);
    });
  });
}
