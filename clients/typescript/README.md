# @routeables/client

The client of [Routeables.SignalR](../../src/Routeables.SignalR): it holds the page the server made current and sends what the user does. It does not depend on Angular or Vue.

```ts
import { RouteablesClient, signalRTransport } from '@routeables/client';

const client = new RouteablesClient(signalRTransport('/routeables'));
client.subscribe(page => render(page));     // page: { target, sequence, route, viewModel, state, commands } or null
client.onFailure(f => console.warn(f.message));

await client.connect();
await client.goTo('/counter');
await client.invoke('increment');           // a [RouteableCommand] of the view model
await client.set('step', 5);                // a public property with a setter
```

`page.state` is a new object after every change, so a framework that compares references sees it. `page.sequence` changes only when another page arrives.

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

The modal is a page of the target `MODAL` (`client.subscribeTo(MODAL, ...)`); it becomes `null` when it closes. After a reconnection the client watches the same targets again.

## Angular and Vue

Turn the subscription into what the framework watches. Nothing of this is in the package yet; [samples/Angular](../../samples/Angular) and [samples/Vue](../../samples/Vue) do it for a whole application:

```ts
// Angular: a signal
const page = signal<Page | null>(null);
client.subscribe(p => page.set(p));

// Vue: a ref
const page = shallowRef<Page | null>(null);
client.subscribe(p => (page.value = p));
```

## Transport

`RouteableTransport` is what the client needs from a connection. `signalRTransport` is the first one; a raw WebSocket can replace it.

After a reconnection the server has a new session with no page: `onConnection(connected)` tells it, and the application goes to its route again.

```
npm install
npm run build
```
