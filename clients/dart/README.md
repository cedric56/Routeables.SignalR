# routeables_client (Dart and Flutter)

The client of [Routeables.SignalR](../../src/Routeables.SignalR) for Dart and Flutter: it holds what the server shows and sends what the user does. It speaks the same protocol (2) as [`@routeables/client`](../typescript), with the same guarantees, and does not depend on Flutter's widgets.

```dart
import 'package:routeables_client/routeables_client.dart';

final client = RouteablesClient(SignalRTransport('http://localhost:5090/routeables'));
client.subscribe((page) => print(page?.state));   // page: target, sequence, route, viewModel, state, commands, generation, version
client.onFailure((failure) => print(failure.message));
client.onError((error) => print(error));

await client.connect();                           // says Hello: the protocol, and the session to resume after a reconnection
await client.goTo('/counter');
await client.invoke('increment');                 // a [RouteableCommand] of the view model
await client.set('step', 5);                      // a property marked [RouteableState(Settable = true)]
```

`page.state` is a new map after every change; what did not change is shared. `page.sequence` changes only when another page arrives (not when the same page is sent again): key a widget on it.

## What the client guarantees

- **Order.** Every target is a stream: a page (a *generation*), then its changes (*versions* 1, 2, …). A change of an older page is dropped; a missing change makes the client ask the whole state again (`Resync`), once.
- **Answers after the state.** When a call completes, the state it changed is already applied.
- **Calls that go on.** A command that asks something (a dialog) or lasts long completes when it ends, not when the server first answers. A route change that asks before leaving a page too.
- **Answers that reached the server.** A dialog leaves `client.dialogs` once the server took the answer (or does not know the dialog any more); when the answer did not reach it, the dialog stays. Notifications likewise.
- **Errors.** A call that does not reach the server completes with `Ack(success: false, code: 'transport')` and is told to `onError`, as is a watch the server refuses.

## Targets, commands, dialogs

```dart
final stop = client.subscribeTo('history', (page) => draw(page));   // a region: watched while somebody subscribes
await client.goToIn('history', 'cats');                              // navigate a region
await client.invokeOn(r'$shell', 'themeCommand');                    // a command of what a target shows

// An ICommand of the state is a map { r'$command': path, 'canExecute': bool }: give it back to run it, at any depth.
final adopt = ((client.page!.state['choices'] as List)[0] as Map)['adoptCommand'];
if (canRun(adopt)) await client.run(pageTarget, adopt);

client.subscribeDialogs((dialogs) => show(dialogs.firstOrNull));     // a dialog the view model waits for
await client.answer(dialog, 0);                                      // the index of the button
client.subscribeToasts((toasts) => show(toasts));
await client.dismiss(toast, click: false);
```

The modal is a page of the target `modalTarget`; it becomes `null` when it closes.

## Reconnection

When the connection drops, the pages stay and `onConnection(false)` is told. When it comes back, the client says `Hello` with the token of its session and the server resumes it: the pages are sent again (same `sequence`), the dialogs that wait come back, and the calls that were running complete when they end — the client names them in `Hello`, so an end told while the connection was gone is told again (`code: 'lost'` if the server forgot it). When the server cannot resume it, what was shown is cleared, the calls that waited complete with `code: 'session-lost'`, `onSessionLost` is told, and the main router goes back to the last route (pass `recover:` to do something else, or `recoverAutomatically: false`).

When the SignalR client stops retrying, the client does not reconnect by itself and the calls that wait keep waiting: call `connect()` again (the session is resumed and they end, or they end as `session-lost`). `disconnect()` is for an application that is done: the calls that wait complete with `code: 'disconnected'`. A notification that has an expiration is dismissed by the client after it (`expireToasts: false` leaves it to the application).

The package is not published on pub.dev yet (`publish_to: none`): depend on it by path or by git (`git: { url: https://github.com/cedric56/Routeables.SignalR.git, path: clients/dart }`).

## Offline data (Routeables Remote Protocol)

`Replica` holds documents written without the server and merges them with it (`MapRouteablesReplication` on the server,
[PROTOCOL-REMOTE.md](../../PROTOCOL-REMOTE.md)), as `@routeables/client` does: the same merge, checked by the same fixture.

```dart
final channel = SignalRReplicationChannel('https://example.com/replication', accessTokenFactory: token);
final replica = await Replica.open(storage, ['Note']);   // a ReplicaStorage: a file, shared preferences...
const note = DocumentId('Note', 'n1');

await replica.edit().create(note).replaceText(note, 'body', controller.text).commit();
replica.find(note)?.text('body');                        // at once, offline
replica.changed.listen((ids) => setState(() {}));
channel.logAdvanced.listen((_) => replica.sync(channel));
await replica.sync(channel);                             // when online; after a failure, call it again
```

## The device of the user: Essentials

With `Routeables.SignalR.Essentials` on the server, the view models use the .NET MAUI Essentials contracts (`IPreferences`,
`IGeolocation`, `IBattery`…) and the calls reach the app of their user. The app gives the services it supports, by name; the client
announces them after each Hello, answers the calls and starts or stops the events. Only what a service lists can be reached.

```dart
final client = RouteablesClient(SignalRTransport(url), essentials: {
  'battery': EssentialsService(
    state: () async => {'chargeLevel': await battery.batteryLevel / 100, 'state': 'Unknown', 'powerSource': 'Unknown', 'energySaverStatus': 'Unknown'},
  ),
  'vibration': EssentialsService(state: () => {'isSupported': true}, methods: {'vibrate': (args) => HapticFeedback.vibrate()}),
});
```

A method takes the arguments of the server in order and returns a JSON value; it throws `EssentialsError(message, code)` for a precise
failure (`EssentialsErrorCodes.permissionDenied`, `notSupported`…). The shapes are those of `browserEssentials()` in `@routeables/client`;
the Flutter sample ([lib/essentials.dart](../../samples/Flutter/xanimals_flutter/lib/essentials.dart)) gives about twenty services with
Flutter plugins.

## Transport

`RouteableTransport` is what the client needs from a connection. `SignalRTransport` uses [signalr_netcore](https://pub.dev/packages/signalr_netcore) (MIT): WebSockets, Server-Sent Events or long polling, automatic reconnection (`retryDelays`), and `accessTokenFactory` for a hub that requires authorization. The SignalR client for Dart does not send a `null` argument: a property is set to `null` only inside a list or a map. `applyPatch(state, patch)` is exported for a transport or a test that applies the patches itself.

## Use it

The package is in this repository, not on pub.dev:

```yaml
dependencies:
  routeables_client:
    path: ../../clients/dart          # or a git dependency on this repository
```

```
flutter pub get
flutter test
```

The tests are the ones of `@routeables/client`, written again in Dart: they drive the client through a fake transport.
