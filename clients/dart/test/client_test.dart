import 'package:flutter_test/flutter_test.dart';
import 'package:routeables_client/routeables_client.dart';

/// A transport the test drives by hand: what the client calls is recorded, what the server tells is [server].
class FakeTransport implements RouteableTransport {
  final List<(String, List<Object?>)> calls = [];
  late ServerMessages server;
  void Function(bool connected) _connection = (_) {};
  Map<String, Object?> reply = {'success': true};
  final Map<String, Map<String, Object?>> replies = {};
  Map<String, Object?> welcome = {'success': true, 'protocol': protocolVersion, 'session': 's1', 'resumed': false};
  bool unreachable = false;

  @override
  Future<void> start(ServerMessages handlers) async {
    server = handlers;
  }

  @override
  Future<void> stop() async {}

  @override
  Future<Object?> call(String method, List<Object?> args) async {
    calls.add((method, args));
    if (unreachable) throw StateError('Not connected.');
    if (method == 'Hello') return welcome;
    return replies[method] ?? reply;
  }

  @override
  void onConnectionChange(void Function(bool connected) handler) => _connection = handler;

  void changeConnection(bool connected) => _connection(connected);

  List<List<Object?>> called(String method) => [for (final (name, args) in calls) if (name == method) args];
}

Future<(RouteablesClient, FakeTransport)> connected({Future<void> Function(Recovery)? recover}) async {
  final transport = FakeTransport();
  final client = RouteablesClient(transport, recover: recover);
  await client.connect();
  return (client, transport);
}

Future<void> tick() => Future<void>.delayed(Duration.zero);

var _generation = 0;

Map<String, Object?> page(String route, {Map<String, Object?> state = const {}, String? target, int? instance}) {
  final generation = ++_generation;
  return {
    'route': route,
    'viewModel': 'CatsViewModel',
    'state': state,
    'commands': ['go'],
    'target': target,
    'generation': generation,
    'instance': instance ?? generation,
  };
}

Map<String, Object?> changed(Map<String, Object?> page, int version, List<Map<String, Object?>> patches) =>
    {'target': page['target'] ?? '', 'generation': page['generation'], 'version': version, 'patches': patches};

Map<String, Object?> setPatch(String property, Object? value) => {'op': 'set', 'path': [property], 'value': value};

const dialog = {'id': 'd1', 'kind': 'message', 'title': 'T', 'header': null, 'body': 'B', 'footer': null, 'showProgress': false, 'buttons': ['Yes', 'No']};
const toast = {'id': 7, 'title': 'Saved', 'message': 'ok', 'type': 'Success', 'expirationSeconds': 6};

void main() {
  group('the page of the main router', () {
    test('is null before the first page, then what the server navigated to', () async {
      final (client, transport) = await connected();
      expect(client.page, isNull);

      transport.server.navigated(page('/cats', state: {'searchText': ''}));

      expect(client.page!.route, '/cats');
      expect(client.page!.viewModel, 'CatsViewModel');
      expect(client.page!.target, pageTarget);
      expect(client.page!.commands, ['go']);
      expect(client.page!.version, 0);
    });

    test('changes state with a new map, and keeps the sequence', () async {
      final (client, transport) = await connected();
      final cats = page('/cats', state: {'searchText': '', 'count': 1});
      transport.server.navigated(cats);
      final before = client.page!;

      transport.server.changed(changed(cats, 1, [setPatch('searchText', 'abys')]));

      expect(identical(client.page, before), isFalse);
      expect(identical(client.page!.state, before.state), isFalse);
      expect(client.page!.state['searchText'], 'abys');
      expect(client.page!.state['count'], 1);
      expect(client.page!.sequence, before.sequence);
      expect(client.page!.version, 1);
    });

    test('gets another sequence when another page arrives, and keeps it when the same page is sent again', () async {
      final (client, transport) = await connected();
      final first = page('/cats');
      transport.server.navigated(first);
      final sequence = client.page!.sequence;

      transport.server.navigated({...first, 'generation': (first['generation'] as int) + 1}); // a resync: the same instance
      expect(client.page!.sequence, sequence);

      transport.server.navigated(page('/cats'));
      expect(client.page!.sequence, isNot(sequence));
    });

    test('never applies a change of a page that was left to the page that replaced it', () async {
      final (client, transport) = await connected();
      final cats = page('/cats', state: {'title': 'cats'});
      final dogs = page('/dogs', state: {'title': 'dogs'});
      transport.server.navigated(cats);
      transport.server.navigated(dogs);

      transport.server.changed(changed(cats, 1, [setPatch('title', 'late cats')]));
      transport.server.navigated(cats); // an older page arriving late

      expect(client.page!.route, '/dogs');
      expect(client.page!.state['title'], 'dogs');
    });

    test('asks the state again when a change is missing, and only once', () async {
      final (client, transport) = await connected();
      final cats = page('/cats', state: {'n': 0});
      transport.server.navigated(cats);

      transport.server.changed(changed(cats, 2, [setPatch('n', 2)]));
      transport.server.changed(changed(cats, 3, [setPatch('n', 3)]));
      await tick();

      expect(client.page!.state['n'], 0);
      expect(transport.called('Resync'), [['']]);
      transport.server.navigated({...cats, 'generation': (cats['generation'] as int) + 1, 'state': {'n': 3}});
      expect(client.page!.state['n'], 3);
    });

    test('ignores a change it already applied', () async {
      final (client, transport) = await connected();
      final cats = page('/cats', state: {'n': 0});
      transport.server.navigated(cats);
      transport.server.changed(changed(cats, 1, [setPatch('n', 1)]));

      transport.server.changed(changed(cats, 1, [setPatch('n', 99)]));

      expect(client.page!.state['n'], 1);
      expect(transport.called('Resync'), isEmpty);
    });

    test('tells the subscribers now and at every change, until they stop', () async {
      final (client, transport) = await connected();
      final seen = <String?>[];
      final stop = client.subscribe((p) => seen.add(p?.route));

      transport.server.navigated(page('/cats'));
      transport.server.navigated(page('/dogs'));
      stop();
      transport.server.navigated(page('/bears'));

      expect(seen, [null, '/cats', '/dogs']);
    });
  });

  group('patches', () {
    test('set a value at a path without changing the maps they came from', () {
      final items = [
        {'name': 'a'},
        {'name': 'b'},
      ];
      final state = <String, Object?>{'items': items, 'title': 't'};

      final next = applyPatch(state, const Patch(op: 'set', path: ['items', 1, 'name'], value: 'B'));

      expect(next, {
        'items': [
          {'name': 'a'},
          {'name': 'B'},
        ],
        'title': 't',
      });
      expect(items[1]['name'], 'b');
      expect(identical((next['items'] as List)[0], items[0]), isTrue); // what did not change is shared
    });

    test('remove and insert items of a list', () {
      final state = <String, Object?>{'words': ['a', 'b', 'c']};

      var next = applyPatch(state, const Patch(op: 'splice', path: ['words'], index: 1, remove: 1, value: ['x', 'y']));
      next = applyPatch(next, const Patch(op: 'splice', path: ['words'], index: 0, remove: 0, value: ['first']));

      expect(next['words'], ['first', 'a', 'x', 'y', 'c']);
      expect(state['words'], ['a', 'b', 'c']);
    });
  });

  group('what the user does', () {
    test('goes to a route, calls a command, sets a property', () async {
      final (client, transport) = await connected();

      await client.goTo('/cats');
      await client.invoke('go', [1, 'two']);
      await client.set('searchText', 'abys');

      expect(transport.called('GoTo'), [['/cats']]);
      expect(transport.called('Invoke'), [
        ['go', [1, 'two']],
      ]);
      expect(transport.called('Set'), [['searchText', 'abys']]);
    });

    test('says hello first, in its protocol', () async {
      final (_, transport) = await connected();

      expect(transport.calls.first.$1, 'Hello');
      expect(transport.called('Hello'), [
        [
          {'protocol': protocolVersion, 'resume': null},
        ],
      ]);
    });

    test('refuses to go on with a server of another protocol', () async {
      final transport = FakeTransport()..welcome = {'success': false, 'protocol': 3, 'session': null, 'resumed': false, 'error': 'update'};
      final client = RouteablesClient(transport);

      await expectLater(client.connect(), throwsA(isA<StateError>()));
    });

    test('always sends the arguments of a command, an empty list when there are none', () async {
      final (client, transport) = await connected();

      await client.invoke('go');
      await client.invokeOn('history', 'push');

      expect(transport.called('Invoke'), [
        ['go', <Object?>[]],
      ]);
      expect(transport.called('InvokeOn'), [
        ['history', 'push', <Object?>[]],
      ]);
    });

    test('returns what the server answered', () async {
      final (client, transport) = await connected();
      transport.reply = {'success': false, 'error': 'no', 'code': 'not-found'};

      final ack = await client.goTo('/x');

      expect(ack.success, isFalse);
      expect(ack.error, 'no');
      expect(ack.code, 'not-found');
    });

    test('waits for the end of a call that goes on, even when the end arrives first', () async {
      final (client, transport) = await connected();
      transport.replies['Invoke'] = {'success': true, 'pending': true, 'operation': 'op1'};

      final call = client.invoke('ask');
      await tick();
      transport.server.completed({'operation': 'op1', 'success': false, 'error': 'refused', 'code': 'failed'});
      final first = await call;
      expect(first.success, isFalse);
      expect(first.error, 'refused');

      transport.replies['Invoke'] = {'success': true, 'pending': true, 'operation': 'op2'};
      transport.server.completed({'operation': 'op2', 'success': true});
      expect((await client.invoke('ask')).success, isTrue);
    });

    test('answers a failure, and tells it, when the call does not reach the server', () async {
      final (client, transport) = await connected();
      final errors = <String>[];
      client.onError((e) => errors.add('${e.method}:${e.code}'));
      transport.unreachable = true;

      final ack = await client.invoke('go');

      expect(ack.success, isFalse);
      expect(ack.code, 'transport');
      expect(errors, ['Invoke:transport']);
    });

    test('goes to a route in a region, and in the main router for the empty target', () async {
      final (client, transport) = await connected();

      await client.goToIn('tabs', 'dogs');
      await client.goToIn(pageTarget, '/cats');

      expect(transport.called('GoToIn'), [['tabs', 'dogs']]);
      expect(transport.called('GoTo'), [['/cats']]);
    });

    test('runs a command of the state by its path', () async {
      final (client, transport) = await connected();
      transport.server.navigated(page('/shop', state: {
        'items': [
          {
            'buy': {r'$command': 'items.0.buy', 'canExecute': true},
          },
        ],
      }));
      final command = ((client.page!.state['items'] as List)[0] as Map)['buy'];

      expect(isCommand(command), isTrue);
      await client.run(pageTarget, command, [5]);

      expect(transport.called('InvokeOn'), [
        ['', 'items.0.buy', [5]],
      ]);
      expect((await client.run(pageTarget, null)).code, 'not-found');
    });

    test('recognises a command token and nothing else', () {
      expect(isCommand({r'$command': 'a', 'canExecute': false}), isTrue);
      expect(isCommand(null), isFalse);
      expect(isCommand('a'), isFalse);
      expect(isCommand({'canExecute': true}), isFalse);
      expect(canRun({r'$command': 'a', 'canExecute': false}), isFalse);
      expect(canRun(null), isTrue);
    });
  });

  group('targets', () {
    test('watches a region while somebody subscribes, once for several subscribers', () async {
      final (client, transport) = await connected();

      final first = client.subscribeTo('history', (_) {});
      final second = client.subscribeTo('history', (_) {});
      await tick();
      expect(transport.called('Watch'), [['history']]);

      first();
      first(); // stopping twice does not stop the other subscriber
      expect(transport.called('Unwatch'), isEmpty);
      second();
      expect(transport.called('Unwatch'), [['history']]);
    });

    test('tells when a watch is refused', () async {
      final (client, transport) = await connected();
      final errors = <String>[];
      client.onError((e) => errors.add('${e.method}:${e.target}:${e.code}'));
      transport.replies['Watch'] = {'success': false, 'error': 'At most 24 targets can be watched.', 'code': 'limit'};

      client.subscribeTo('r25', (_) {});
      await tick();
      await tick();

      expect(errors, ['Watch:r25:limit']);
    });

    test('does not ask to watch the main page nor the modal', () async {
      final (client, transport) = await connected();

      client.subscribe((_) {});
      client.subscribeTo(modalTarget, (_) {});

      expect(transport.called('Watch'), isEmpty);
    });

    test('keeps the pages of the targets apart', () async {
      final (client, transport) = await connected();
      final regions = <String?>[];
      client.subscribeTo('side', (p) => regions.add(p?.route));

      transport.server.navigated(page('/cats', state: {'a': 1}));
      final side = page('/dogs', state: {'b': 2}, target: 'side');
      transport.server.navigated(side);
      transport.server.changed(changed(side, 1, [setPatch('b', 3)]));

      expect(client.page!.route, '/cats');
      expect(client.pageOf('side')!.route, '/dogs');
      expect(client.pageOf('side')!.state['b'], 3);
      expect(client.page!.state['b'], isNull);
      expect(client.pageOf('nowhere'), isNull);
      expect(regions, [null, '/dogs', '/dogs']);
    });

    test('forgets the page of a region nobody watches', () async {
      final (client, transport) = await connected();
      final stop = client.subscribeTo('side', (_) {});
      transport.server.navigated(page('/dogs', target: 'side'));

      stop();

      expect(client.pageOf('side'), isNull);
    });

    test('shows the modal as a page of its target, until it closes', () async {
      final (client, transport) = await connected();
      final seen = <String?>[];
      client.subscribeTo(modalTarget, (p) => seen.add(p?.viewModel));

      final note = {...page('', state: {'note': ''}, target: modalTarget), 'viewModel': 'NoteViewModel'};
      transport.server.navigated(note);
      transport.server.closed({'target': modalTarget, 'generation': note['generation']});

      expect(seen, [null, 'NoteViewModel', null]);
      expect(client.pageOf(modalTarget), isNull);
    });

    test('does not close a page with the close of an older one', () async {
      final (client, transport) = await connected();
      final first = page('', target: modalTarget);
      final second = page('', target: modalTarget);
      transport.server.navigated(first);
      transport.server.navigated(second);

      transport.server.closed({'target': modalTarget, 'generation': first['generation']});

      expect(client.pageOf(modalTarget), isNotNull);
    });

    test('sets a property of a target', () async {
      final (client, transport) = await connected();

      await client.setOn(modalTarget, 'note', 'hi');

      expect(transport.called('SetOn'), [[modalTarget, 'note', 'hi']]);
    });
  });

  group('dialogs and notifications', () {
    test('keeps the dialogs that wait, in order, and removes the one the server took the answer of', () async {
      final (client, transport) = await connected();
      final counts = <int>[];
      client.subscribeDialogs((d) => counts.add(d.length));

      transport.server.dialogRequested(dialog);
      transport.server.dialogRequested({...dialog, 'id': 'd2'});
      transport.server.dialogRequested(dialog); // sent again by a resumed session
      await client.answer(client.dialogs.first, 1);

      expect(counts, [0, 1, 2, 1]);
      expect([for (final d in client.dialogs) d.id], ['d2']);
      expect(transport.called('Answer'), [['d1', 1]]);
    });

    test('keeps a dialog whose answer did not reach the server, and drops one the server does not know', () async {
      final (client, transport) = await connected();
      transport.server.dialogRequested(dialog);
      transport.server.dialogRequested({...dialog, 'id': 'd2'});

      transport.unreachable = true;
      expect((await client.answer(client.dialogs.first, 0)).success, isFalse);
      expect([for (final d in client.dialogs) d.id], ['d1', 'd2']);

      transport.unreachable = false;
      transport.replies['Answer'] = {'success': false, 'error': 'No such dialog is waiting for an answer.', 'code': 'not-found'};
      await client.answer(client.dialogs.last, 0);
      expect([for (final d in client.dialogs) d.id], ['d1']);
    });

    test('keeps the notifications and tells when one is closed or clicked', () async {
      final (client, transport) = await connected();
      transport.server.notified(toast);
      transport.server.notified({...toast, 'id': 8});
      expect(client.toasts.length, 2);

      await client.dismiss(client.toasts.first, click: true);
      await client.dismiss(client.toasts.first);

      expect(client.toasts, isEmpty);
      expect(transport.called('Dismiss'), [
        [7, true],
        [8, false],
      ]);
    });

    test('keeps a notification whose dismissal did not reach the server', () async {
      final (client, transport) = await connected();
      transport.server.notified(toast);
      transport.unreachable = true;

      await client.dismiss(client.toasts.first);

      expect(client.toasts.length, 1);
    });
  });

  group('the connection', () {
    test('keeps what is shown while the connection is down, and resumes the session when it comes back', () async {
      final (client, transport) = await connected();
      client.subscribeTo('history', (_) {});
      transport.server.navigated(page('/cats'));
      transport.server.dialogRequested({...dialog, 'id': 'd'});
      final states = <bool>[];
      client.onConnection(states.add);

      transport.changeConnection(false);
      expect(client.isConnected, isFalse);
      expect(client.page!.route, '/cats');

      transport.welcome = {'success': true, 'protocol': protocolVersion, 'session': 's1', 'resumed': true};
      transport.changeConnection(true);
      await tick();

      expect(states, [false, true]);
      expect(transport.called('Hello').last, [
        {'protocol': protocolVersion, 'resume': 's1'},
      ]);
      expect(client.page!.route, '/cats');
      expect(client.dialogs.length, 1);
      expect(transport.called('Watch'), [['history']]); // the resumed session watches it already
      expect(transport.called('GoTo'), isEmpty);
    });

    test('starts again from the last route when the session could not be resumed', () async {
      final (client, transport) = await connected();
      client.subscribeTo('history', (_) {});
      transport.server.navigated(page('/cats'));
      transport.server.notified({...toast, 'id': 1, 'expirationSeconds': null});
      transport.replies['Invoke'] = {'success': true, 'pending': true, 'operation': 'op9'};
      final waiting = client.invoke('slow');
      await tick();
      final lost = <String?>[];
      client.onSessionLost((r) => lost.add(r.lastRoute));

      transport.changeConnection(false);
      transport.welcome = {'success': true, 'protocol': protocolVersion, 'session': 's2', 'resumed': false};
      transport.changeConnection(true);
      await tick();
      await tick();

      expect(lost, ['/cats']);
      expect((await waiting).code, 'session-lost');
      expect(client.page, isNull);
      expect(client.toasts, isEmpty);
      expect(transport.called('Watch'), [['history'], ['history']]);
      expect(transport.called('GoTo'), [['/cats']]);
    });

    test('lets the application recover by itself', () async {
      final recovered = <String?>[];
      final (client, transport) = await connected(recover: (r) async => recovered.add(r.lastRoute));
      transport.server.navigated(page('/dogs'));

      transport.changeConnection(false);
      transport.welcome = {'success': true, 'protocol': protocolVersion, 'session': 's2', 'resumed': false};
      transport.changeConnection(true);
      await tick();

      expect(recovered, ['/dogs']);
      expect(transport.called('GoTo'), isEmpty);
      expect(client.isConnected, isTrue);
    });

    test('tells the failures of the server', () async {
      final (client, transport) = await connected();
      final failures = <String>[];
      client.onFailure((f) => failures.add('${f.route}:${f.message}'));

      transport.server.failed({'route': '/x', 'message': 'No such page'});

      expect(failures, ['/x:No such page']);
    });
  });
}
