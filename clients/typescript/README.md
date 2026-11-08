# @routeables/client

The client of [Routeables.SignalR](../../src/Routeables.SignalR): it holds what the server shows and sends what the user does. It does not depend on Angular or Vue.

```ts
import { RouteablesClient, signalRTransport } from '@routeables/client';

const client = new RouteablesClient(signalRTransport('/routeables'));
client.subscribe(page => render(page));     // page: { target, sequence, route, viewModel, state, commands, generation, version } or null
client.onFailure(f => console.warn(f.message));
client.onError(e => console.warn(e.method, e.message));

await client.connect();                     // says Hello: the protocol, and the session to resume after a reconnection
await client.goTo('/counter');
await client.invoke('increment');           // a [RouteableCommand] of the view model
await client.set('step', 5);                // a property marked [RouteableState(Settable = true)]
```

`page.state` is a new object after every change, so a framework that compares references sees it; what did not change is shared. `page.sequence` changes only when another page arrives (not when the same page is sent again).

## What the client guarantees

- **Order.** Every target is a stream: a page (a *generation*), then its changes (*versions* 1, 2, …). A change of an older page is dropped; a missing change makes the client ask the whole state again (`Resync`), once.
- **Answers after the state.** When a call resolves, the state it changed is already applied.
- **Calls that go on.** A command that asks something (a dialog) or lasts long resolves when it ends, not when the server first answers. A route change that asks before leaving a page too.
- **Answers that reached the server.** A dialog leaves `client.dialogs` once the server took the answer (or does not know the dialog any more); when the answer did not reach it, the dialog stays, to be answered again. Notifications likewise.
- **Errors.** A call that does not reach the server resolves `{ success: false, code: 'transport' }` and is told to `onError`, as is a watch the server refuses (a limit, an unknown target).

## Targets, commands, dialogs

The server shows several things at once (see "Targets" in Routeables.SignalR): the main page (`""`), the modal (`MODAL`), the regions (by name) and the view models it registered (`$name`).

```ts
const off = client.subscribeTo('history', page => draw(page));   // a region: watched while somebody subscribes
await client.goToIn('history', 'cats');                          // navigate a region
await client.invokeOn('$shell', 'themeCommand');                 // a command of what a target shows

// An ICommand of the state is a token { $command, canExecute }: give it back to run it, at any depth.
const adopt = client.pageOf('')!.state['choices'][0].adoptCommand;
if (isCommand(adopt) && adopt.canExecute) await client.run('', adopt);

client.subscribeDialogs(dialogs => show(dialogs[0]));            // a dialog the view model waits for
await client.answer(dialog, 0);                                  // the index of the button
client.subscribeToasts(toasts => show(toasts));
await client.dismiss(toast, /* click */ false);
```

The modal is a page of the target `MODAL` (`client.subscribeTo(MODAL, ...)`); it becomes `null` when it closes.

## Reconnection

When the connection drops, the pages stay on screen and `onConnection(false)` is told. When it comes back, the client says `Hello` with the token of its session and the server resumes it: the pages are sent again (same `sequence`: components are kept), the dialogs that wait come back, and the calls that were running resolve when they end — the client names them in `Hello`, so an end told while the connection was gone is told again (`{ code: 'lost' }` if the server forgot it).

When the server cannot resume it (the grace period ended, the server restarted), the view models are new: what was shown is cleared, the calls that waited resolve `{ code: 'session-lost' }`, `onSessionLost` is told, and the main router goes back to the last route. Pass `recover` to do something else:

```ts
const client = new RouteablesClient(signalRTransport('/routeables'), {
  recover: async ({ client, lastRoute }) => { await client.goTo(lastRoute ?? '/'); },   // or false: nothing
});
```

## Angular and Vue

Turn the subscription into what the framework watches. [samples/Angular](../../samples/Angular) and [samples/Vue](../../samples/Vue) do it for a whole application:

```ts
// Angular: a signal
const page = signal<Page | null>(null);
client.subscribe(p => page.set(p));

// Vue: a ref
const page = shallowRef<Page | null>(null);
client.subscribe(p => (page.value = p));
```

## Transport

`RouteableTransport` is what the client needs from a connection. `signalRTransport` is the first one (`reconnect` sets the delays of SignalR's automatic reconnection); a raw WebSocket can replace it. `apply(state, patch)` is exported for a transport or a test that applies the patches itself.

```
npm ci
npm test
npm run build
```

In Visual Studio, the client is the project `Routeables.Client` of `Routeables.SignalR.slnx` (`Routeables.Client.esproj`): Build runs `npm run build`. Its tests use the test runner of Node (`node:test`), which the Test Explorer does not list: run `npm test`.

The package shares its version with the NuGet package Routeables.SignalR (see [VERSIONING.md](../../VERSIONING.md)).
