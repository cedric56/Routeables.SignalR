import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { MODAL, PAGE, RouteablesClient, isCommand } from '../src/index.js';
import type { Ack, Dialog, RouteableTransport, ServerMessages, ServerMethod, Toast } from '../src/index.js';

/** A transport the test drives by hand: what the client calls is recorded, what the server tells is `server`. */
class FakeTransport implements RouteableTransport {
  readonly calls: { method: ServerMethod; args: unknown[] }[] = [];
  server!: ServerMessages;
  private connection: (connected: boolean) => void = () => {};
  reply: Ack = { success: true };

  async start(handlers: ServerMessages): Promise<void> {
    this.server = handlers;
  }
  async stop(): Promise<void> {}
  async call(method: ServerMethod, ...args: unknown[]): Promise<Ack> {
    this.calls.push({ method, args });
    return this.reply;
  }
  onConnectionChange(handler: (connected: boolean) => void): void {
    this.connection = handler;
  }
  changeConnection(connected: boolean): void {
    this.connection(connected);
  }
  called(method: ServerMethod): unknown[][] {
    return this.calls.filter(c => c.method === method).map(c => c.args);
  }
}

async function connected(): Promise<{ client: RouteablesClient; transport: FakeTransport }> {
  const transport = new FakeTransport();
  const client = new RouteablesClient(transport);
  await client.connect();
  return { client, transport };
}

const page = (route: string, state: Record<string, unknown> = {}, target?: string) => ({
  route, viewModel: 'CatsViewModel', state, commands: ['go'], target,
});

describe('the page of the main router', () => {
  it('is null before the first page, then what the server navigated to', async () => {
    const { client, transport } = await connected();
    assert.equal(client.page, null);

    transport.server.navigated(page('/cats', { searchText: '' }));

    const shown = client.pageOf(PAGE);
    assert.equal(shown?.route, '/cats');
    assert.equal(shown?.viewModel, 'CatsViewModel');
    assert.equal(shown?.target, PAGE);
    assert.deepEqual(shown?.commands, ['go']);
  });

  it('changes state with a new object, and keeps the sequence', async () => {
    const { client, transport } = await connected();
    transport.server.navigated(page('/cats', { searchText: '', count: 1 }));
    const before = client.page!;

    transport.server.changed({ property: 'searchText', value: 'abys' });

    assert.notEqual(client.page, before);
    assert.notEqual(client.page!.state, before.state);
    assert.equal(client.page!.state['searchText'], 'abys');
    assert.equal(client.page!.state['count'], 1);
    assert.equal(client.page!.sequence, before.sequence);
  });

  it('gets another sequence when another page arrives', async () => {
    const { client, transport } = await connected();
    transport.server.navigated(page('/cats'));
    const first = client.page!.sequence;

    transport.server.navigated(page('/cats'));

    assert.notEqual(client.page!.sequence, first);
  });

  it('ignores a change when there is no page', async () => {
    const { client, transport } = await connected();

    transport.server.changed({ property: 'x', value: 1 });

    assert.equal(client.page, null);
  });

  it('tells the subscribers now and at every change, until they stop', async () => {
    const { client, transport } = await connected();
    const seen: (string | undefined)[] = [];
    const stop = client.subscribe(p => seen.push(p?.route));

    transport.server.navigated(page('/cats'));
    transport.server.navigated(page('/dogs'));
    stop();
    transport.server.navigated(page('/bears'));

    assert.deepEqual(seen, [undefined, '/cats', '/dogs']);
  });
});

describe('what the user does', () => {
  it('goes to a route, calls a command, sets a property', async () => {
    const { client, transport } = await connected();

    await client.goTo('/cats');
    await client.invoke('go', 1, 'two');
    await client.set('searchText', 'abys');

    assert.deepEqual(transport.called('GoTo'), [['/cats']]);
    assert.deepEqual(transport.called('Invoke'), [['go', [1, 'two']]]);
    assert.deepEqual(transport.called('Set'), [['searchText', 'abys']]);
  });

  it('always sends the arguments of a command, an empty list when there are none', async () => {
    const { client, transport } = await connected();

    await client.invoke('go');
    await client.invokeOn('history', 'push');

    assert.deepEqual(transport.called('Invoke'), [['go', []]]);
    assert.deepEqual(transport.called('InvokeOn'), [['history', 'push', []]]);
  });

  it('returns what the server answered', async () => {
    const { client, transport } = await connected();
    transport.reply = { success: false, error: 'no' };

    assert.deepEqual(await client.goTo('/x'), { success: false, error: 'no' });
  });

  it('goes to a route in a region, and in the main router for the empty target', async () => {
    const { client, transport } = await connected();

    await client.goToIn('tabs', 'dogs');
    await client.goToIn(PAGE, '/cats');

    assert.deepEqual(transport.called('GoToIn'), [['tabs', 'dogs']]);
    assert.deepEqual(transport.called('GoTo'), [['/cats']]);
  });

  it('runs a command of the state by its path', async () => {
    const { client, transport } = await connected();
    const state = { items: [{ buy: { $command: 'items.0.buy', canExecute: true } }] };
    transport.server.navigated(page('/shop', state));
    const command = (client.page!.state['items'] as typeof state.items)[0]!.buy;

    assert.ok(isCommand(command));
    await client.run(PAGE, command, 5);

    assert.deepEqual(transport.called('InvokeOn'), [['', 'items.0.buy', [5]]]);
  });

  it('recognises a command token and nothing else', () => {
    assert.ok(isCommand({ $command: 'a', canExecute: false }));
    assert.ok(!isCommand(null));
    assert.ok(!isCommand('a'));
    assert.ok(!isCommand({ canExecute: true }));
  });
});

describe('targets', () => {
  it('watches a region while somebody subscribes, once for several subscribers', async () => {
    const { client, transport } = await connected();

    const first = client.subscribeTo('history', () => {});
    const second = client.subscribeTo('history', () => {});
    assert.deepEqual(transport.called('Watch'), [['history']]);

    first();
    assert.deepEqual(transport.called('Unwatch'), []);
    second();
    assert.deepEqual(transport.called('Unwatch'), [['history']]);
  });

  it('does not ask to watch the main page nor the modal', async () => {
    const { client, transport } = await connected();

    client.subscribe(() => {});
    client.subscribeTo(MODAL, () => {});

    assert.deepEqual(transport.called('Watch'), []);
  });

  it('keeps the pages of the targets apart', async () => {
    const { client, transport } = await connected();
    const regions: (string | undefined)[] = [];
    client.subscribeTo('side', p => regions.push(p?.route));

    transport.server.navigated(page('/cats', { a: 1 }));
    transport.server.navigated(page('/dogs', { b: 2 }, 'side'));
    transport.server.changed({ property: 'b', value: 3, target: 'side' });

    assert.equal(client.page!.route, '/cats');
    assert.equal(client.pageOf('side')!.route, '/dogs');
    assert.equal(client.pageOf('side')!.state['b'], 3);
    assert.equal(client.page!.state['b'], undefined);
    assert.equal(client.pageOf('nowhere'), null);
    assert.deepEqual(regions, [undefined, '/dogs', '/dogs']);
  });

  it('forgets the page of a region nobody watches', async () => {
    const { client, transport } = await connected();
    const stop = client.subscribeTo('side', () => {});
    transport.server.navigated(page('/dogs', {}, 'side'));

    stop();

    assert.equal(client.pageOf('side'), null);
  });

  it('shows the modal as a page of its target, until it closes', async () => {
    const { client, transport } = await connected();
    const seen: (string | undefined)[] = [];
    client.subscribeTo(MODAL, p => seen.push(p?.viewModel));

    transport.server.navigated({ route: '', viewModel: 'NoteViewModel', state: { note: '' }, commands: [], target: MODAL });
    transport.server.closed({ target: MODAL });

    assert.deepEqual(seen, [undefined, 'NoteViewModel', undefined]);
    assert.equal(client.pageOf(MODAL), null);
  });

  it('sets a property of a target', async () => {
    const { client, transport } = await connected();

    await client.setOn(MODAL, 'note', 'hi');

    assert.deepEqual(transport.called('SetOn'), [[MODAL, 'note', 'hi']]);
  });
});

describe('dialogs and notifications', () => {
  const dialog: Dialog = { id: 'd1', kind: 'message', title: 'T', header: null, body: 'B', footer: null, showProgress: false, buttons: ['Yes', 'No'] };
  const toast: Toast = { id: 7, title: 'Saved', message: 'ok', type: 'Success', expirationSeconds: 6 };

  it('keeps the dialogs that wait, in order, and removes the one that was answered', async () => {
    const { client, transport } = await connected();
    const counts: number[] = [];
    client.subscribeDialogs(d => counts.push(d.length));

    transport.server.dialogRequested(dialog);
    transport.server.dialogRequested({ ...dialog, id: 'd2' });
    await client.answer(dialog, 1);

    assert.deepEqual(counts, [0, 1, 2, 1]);
    assert.deepEqual(client.dialogs.map(d => d.id), ['d2']);
    assert.deepEqual(transport.called('Answer'), [['d1', 1]]);
  });

  it('keeps the notifications and tells when one is closed or clicked', async () => {
    const { client, transport } = await connected();
    transport.server.notified(toast);
    transport.server.notified({ ...toast, id: 8 });
    assert.equal(client.toasts.length, 2);

    await client.dismiss(toast, true);
    await client.dismiss({ ...toast, id: 8 });

    assert.equal(client.toasts.length, 0);
    assert.deepEqual(transport.called('Dismiss'), [[7, true], [8, false]]);
  });
});

describe('the connection', () => {
  it('forgets what was shown when the connection drops, and watches the targets again when it comes back', async () => {
    const { client, transport } = await connected();
    client.subscribeTo('history', () => {});
    transport.server.navigated(page('/cats'));
    transport.server.navigated(page('/dogs', {}, 'history'));
    transport.server.dialogRequested({ id: 'd', kind: 'message', title: 'T', header: null, body: null, footer: null, showProgress: false, buttons: [] });
    transport.server.notified({ id: 1, title: 'T', message: 'M', type: 'Information', expirationSeconds: null });
    const states: boolean[] = [];
    client.onConnection(c => states.push(c));

    transport.changeConnection(false);

    assert.equal(client.page, null);
    assert.equal(client.pageOf('history'), null);
    assert.equal(client.dialogs.length, 0);
    assert.equal(client.toasts.length, 0);

    transport.changeConnection(true);

    assert.deepEqual(states, [false, true]);
    assert.deepEqual(transport.called('Watch'), [['history'], ['history']]);
  });

  it('tells the failures of the server', async () => {
    const { client, transport } = await connected();
    const failures: string[] = [];
    client.onFailure(f => failures.push(f.route + ':' + f.message));

    transport.server.failed({ route: '/x', message: 'No such page' });

    assert.deepEqual(failures, ['/x:No such page']);
  });
});
