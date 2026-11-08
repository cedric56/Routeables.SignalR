import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { MODAL, PAGE, PROTOCOL, RouteablesClient, apply, isCommand } from '../src/index.js';
import type { Ack, Dialog, NavigatedMessage, RouteableTransport, ServerMessages, ServerMethod, Toast, Welcome } from '../src/index.js';

/** A transport the test drives by hand: what the client calls is recorded, what the server tells is `server`. */
class FakeTransport implements RouteableTransport {
  readonly calls: { method: ServerMethod; args: unknown[] }[] = [];
  server!: ServerMessages;
  private connection: (connected: boolean) => void = () => {};
  reply: Ack = { success: true };
  replies = new Map<ServerMethod, Ack>();
  welcome: Welcome = { success: true, protocol: PROTOCOL, session: 's1', resumed: false };
  unreachable = false;

  async start(handlers: ServerMessages): Promise<void> {
    this.server = handlers;
  }
  async stop(): Promise<void> {}
  async call<T>(method: ServerMethod, ...args: unknown[]): Promise<T> {
    this.calls.push({ method, args });
    if (this.unreachable) throw new Error('Not connected.');
    if (method === 'Hello') return this.welcome as T;
    return (this.replies.get(method) ?? this.reply) as T;
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

async function connected(options?: ConstructorParameters<typeof RouteablesClient>[1]): Promise<{ client: RouteablesClient; transport: FakeTransport }> {
  const transport = new FakeTransport();
  const client = new RouteablesClient(transport, options);
  await client.connect();
  return { client, transport };
}

const tick = () => new Promise(resolve => setTimeout(resolve, 0));

let generation = 0;
const page = (route: string, state: Record<string, unknown> = {}, target?: string, instance = ++generation): NavigatedMessage => ({
  route, viewModel: 'CatsViewModel', state, commands: ['go'], target, generation: ++generation, instance,
});
const set = (property: string, value: unknown) => ({ op: 'set' as const, path: [property], value });

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
    assert.equal(shown?.version, 0);
  });

  it('changes state with a new object, and keeps the sequence', async () => {
    const { client, transport } = await connected();
    const cats = page('/cats', { searchText: '', count: 1 });
    transport.server.navigated(cats);
    const before = client.page!;

    transport.server.changed({ target: PAGE, generation: cats.generation, version: 1, patches: [set('searchText', 'abys')] });

    assert.notEqual(client.page, before);
    assert.notEqual(client.page!.state, before.state);
    assert.equal(client.page!.state['searchText'], 'abys');
    assert.equal(client.page!.state['count'], 1);
    assert.equal(client.page!.sequence, before.sequence);
    assert.equal(client.page!.version, 1);
  });

  it('gets another sequence when another page arrives, and keeps it when the same page is sent again', async () => {
    const { client, transport } = await connected();
    const first = page('/cats');
    transport.server.navigated(first);
    const sequence = client.page!.sequence;

    transport.server.navigated({ ...first, generation: first.generation + 1 });   // a resync: the same instance
    assert.equal(client.page!.sequence, sequence);

    transport.server.navigated(page('/cats'));
    assert.notEqual(client.page!.sequence, sequence);
  });

  it('ignores a change when there is no page', async () => {
    const { client, transport } = await connected();

    transport.server.changed({ target: PAGE, generation: 1, version: 1, patches: [set('x', 1)] });

    assert.equal(client.page, null);
  });

  it('never applies a change of a page that was left to the page that replaced it', async () => {
    const { client, transport } = await connected();
    const cats = page('/cats', { title: 'cats' });
    const dogs = page('/dogs', { title: 'dogs' });
    transport.server.navigated(cats);
    transport.server.navigated(dogs);

    transport.server.changed({ target: PAGE, generation: cats.generation, version: 1, patches: [set('title', 'late cats')] });
    transport.server.navigated(cats);   // an older page arriving late

    assert.equal(client.page!.route, '/dogs');
    assert.equal(client.page!.state['title'], 'dogs');
  });

  it('asks the state again when a change is missing, and only once', async () => {
    const { client, transport } = await connected();
    const cats = page('/cats', { n: 0 });
    transport.server.navigated(cats);

    transport.server.changed({ target: PAGE, generation: cats.generation, version: 2, patches: [set('n', 2)] });
    transport.server.changed({ target: PAGE, generation: cats.generation, version: 3, patches: [set('n', 3)] });

    assert.equal(client.page!.state['n'], 0);
    assert.deepEqual(transport.called('Resync'), [['']]);
    transport.server.navigated({ ...cats, generation: cats.generation + 1, state: { n: 3 } });
    assert.equal(client.page!.state['n'], 3);
  });

  it('ignores a change it already applied', async () => {
    const { client, transport } = await connected();
    const cats = page('/cats', { n: 0 });
    transport.server.navigated(cats);
    transport.server.changed({ target: PAGE, generation: cats.generation, version: 1, patches: [set('n', 1)] });

    transport.server.changed({ target: PAGE, generation: cats.generation, version: 1, patches: [set('n', 99)] });

    assert.equal(client.page!.state['n'], 1);
    assert.deepEqual(transport.called('Resync'), []);
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

describe('patches', () => {
  it('sets a value at a path without changing the objects it came from', () => {
    const state = { items: [{ name: 'a' }, { name: 'b' }], title: 't' };

    const next = apply(state, { op: 'set', path: ['items', 1, 'name'], value: 'B' });

    assert.deepEqual(next, { items: [{ name: 'a' }, { name: 'B' }], title: 't' });
    assert.equal(state.items[1]!.name, 'b');
    assert.equal((next['items'] as unknown[])[0], state.items[0]);   // what did not change is shared
  });

  it('removes and inserts items of an array', () => {
    const state = { words: ['a', 'b', 'c'] };

    let next = apply(state, { op: 'splice', path: ['words'], index: 1, remove: 1, value: ['x', 'y'] });
    next = apply(next, { op: 'splice', path: ['words'], index: 0, remove: 0, value: ['first'] });

    assert.deepEqual(next['words'], ['first', 'a', 'x', 'y', 'c']);
    assert.deepEqual(state.words, ['a', 'b', 'c']);
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

  it('says hello first, in its protocol', async () => {
    const { transport } = await connected();

    assert.equal(transport.calls[0]!.method, 'Hello');
    assert.deepEqual(transport.called('Hello'), [[{ protocol: PROTOCOL, resume: null, waiting: [] }]]);
  });

  it('refuses to go on with a server of another protocol', async () => {
    const transport = new FakeTransport();
    transport.welcome = { success: false, protocol: 3, session: null, resumed: false, error: 'update' };
    const client = new RouteablesClient(transport);

    await assert.rejects(client.connect(), /update/);
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

  it('waits for the end of a call that goes on, even when the end arrives first', async () => {
    const { client, transport } = await connected();
    transport.replies.set('Invoke', { success: true, pending: true, operation: 'op1' });

    const call = client.invoke('ask');
    await tick();
    transport.server.completed({ operation: 'op1', success: false, error: 'refused', code: 'failed' });
    assert.deepEqual(await call, { success: false, error: 'refused', code: 'failed' });

    transport.replies.set('Invoke', { success: true, pending: true, operation: 'op2' });
    transport.server.completed({ operation: 'op2', success: true });
    assert.deepEqual(await client.invoke('ask'), { success: true, error: undefined, code: undefined });
  });

  it('answers a failure, and tells it, when the call does not reach the server', async () => {
    const { client, transport } = await connected();
    const errors: string[] = [];
    client.onError(e => errors.push(`${e.method}:${e.code}`));
    transport.unreachable = true;

    const ack = await client.invoke('go');

    assert.equal(ack.success, false);
    assert.equal(ack.code, 'transport');
    assert.deepEqual(errors, ['Invoke:transport']);
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
    first();   // stopping twice does not stop the other subscriber
    assert.deepEqual(transport.called('Unwatch'), []);
    second();
    assert.deepEqual(transport.called('Unwatch'), [['history']]);
  });

  it('tells when a watch is refused', async () => {
    const { client, transport } = await connected();
    const errors: string[] = [];
    client.onError(e => errors.push(`${e.method}:${e.target}:${e.code}`));
    transport.replies.set('Watch', { success: false, error: 'At most 24 targets can be watched.', code: 'limit' });

    client.subscribeTo('r25', () => {});
    await tick();

    assert.deepEqual(errors, ['Watch:r25:limit']);
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
    const side = page('/dogs', { b: 2 }, 'side');
    transport.server.navigated(side);
    transport.server.changed({ target: 'side', generation: side.generation, version: 1, patches: [set('b', 3)] });

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

    const modal = { ...page('', { note: '' }, MODAL), viewModel: 'NoteViewModel' };
    transport.server.navigated(modal);
    transport.server.closed({ target: MODAL, generation: modal.generation });

    assert.deepEqual(seen, [undefined, 'NoteViewModel', undefined]);
    assert.equal(client.pageOf(MODAL), null);
  });

  it('does not close a page with the close of an older one', async () => {
    const { client, transport } = await connected();
    const first = page('', {}, MODAL);
    const second = page('', {}, MODAL);
    transport.server.navigated(first);
    transport.server.navigated(second);

    transport.server.closed({ target: MODAL, generation: first.generation });

    assert.notEqual(client.pageOf(MODAL), null);
  });

  it('sets a property of a target', async () => {
    const { client, transport } = await connected();

    await client.setOn(MODAL, 'note', 'hi');

    assert.deepEqual(transport.called('SetOn'), [[MODAL, 'note', 'hi']]);
  });
});

describe('dialogs and notifications', () => {
  const dialog: Dialog = { id: 'd1', kind: 'message', title: 'T', header: null, body: 'B', footer: null, showProgress: false, buttons: ['Yes', 'No'] };
  const toast: Toast = { id: 7, title: 'Saved', message: 'ok', type: 'Success', expirationSeconds: null };

  it('closes a notification after its time, unless told not to', async () => {
    const { transport } = await connected();
    transport.server.notified({ ...toast, id: 3, expirationSeconds: 0.01 });
    await new Promise(resolve => setTimeout(resolve, 50));
    assert.deepEqual(transport.called('Dismiss'), [[3, false]]);

    const kept = await connected({ expireToasts: false });
    kept.transport.server.notified({ ...toast, id: 4, expirationSeconds: 0.01 });
    await new Promise(resolve => setTimeout(resolve, 50));
    assert.deepEqual(kept.transport.called('Dismiss'), []);
  });

  it('keeps the dialogs that wait, in order, and removes the one the server took the answer of', async () => {
    const { client, transport } = await connected();
    const counts: number[] = [];
    client.subscribeDialogs(d => counts.push(d.length));

    transport.server.dialogRequested(dialog);
    transport.server.dialogRequested({ ...dialog, id: 'd2' });
    transport.server.dialogRequested(dialog);   // sent again by a resumed session
    await client.answer(dialog, 1);

    assert.deepEqual(counts, [0, 1, 2, 1]);
    assert.deepEqual(client.dialogs.map(d => d.id), ['d2']);
    assert.deepEqual(transport.called('Answer'), [['d1', 1]]);
  });

  it('keeps a dialog whose answer did not reach the server, and drops one the server does not know', async () => {
    const { client, transport } = await connected();
    transport.server.dialogRequested(dialog);
    transport.server.dialogRequested({ ...dialog, id: 'd2' });

    transport.unreachable = true;
    assert.equal((await client.answer(dialog, 0)).success, false);
    assert.deepEqual(client.dialogs.map(d => d.id), ['d1', 'd2']);

    transport.unreachable = false;
    transport.replies.set('Answer', { success: false, error: 'No such dialog is waiting for an answer.', code: 'not-found' });
    await client.answer({ ...dialog, id: 'd2' }, 0);
    assert.deepEqual(client.dialogs.map(d => d.id), ['d1']);
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

  it('keeps a notification whose dismissal did not reach the server', async () => {
    const { client, transport } = await connected();
    transport.server.notified(toast);
    transport.unreachable = true;

    await client.dismiss(toast);

    assert.equal(client.toasts.length, 1);
  });
});

describe('the connection', () => {
  it('keeps what is shown while the connection is down, and resumes the session when it comes back', async () => {
    const { client, transport } = await connected();
    client.subscribeTo('history', () => {});
    transport.server.navigated(page('/cats'));
    transport.server.dialogRequested({ id: 'd', kind: 'message', title: 'T', header: null, body: null, footer: null, showProgress: false, buttons: [] });
    const states: boolean[] = [];
    client.onConnection(c => states.push(c));

    transport.changeConnection(false);
    assert.equal(client.isConnected, false);
    assert.equal(client.page!.route, '/cats');

    transport.welcome = { success: true, protocol: PROTOCOL, session: 's1', resumed: true };
    transport.changeConnection(true);
    await tick();

    assert.deepEqual(states, [false, true]);
    assert.deepEqual(transport.called('Hello').at(-1), [{ protocol: PROTOCOL, resume: 's1', waiting: [] }]);
    assert.equal(client.page!.route, '/cats');
    assert.equal(client.dialogs.length, 1);
    assert.deepEqual(transport.called('Watch'), [['history']]);   // the resumed session watches it already
    assert.deepEqual(transport.called('GoTo'), []);
  });

  it('names the calls it waits for when it resumes its session, and gets their end then', async () => {
    const { client, transport } = await connected();
    transport.replies.set('Invoke', { success: true, pending: true, operation: 'op4' });
    let ended: Ack | null = null;
    void client.invoke('slow').then(ack => { ended = ack; });
    await tick();

    transport.changeConnection(false);
    transport.welcome = { success: true, protocol: PROTOCOL, session: 's1', resumed: true };
    transport.changeConnection(true);
    await tick();
    assert.deepEqual(transport.called('Hello').at(-1), [{ protocol: PROTOCOL, resume: 's1', waiting: ['op4'] }]);
    assert.equal(ended, null);

    transport.server.completed({ operation: 'op4', success: false, error: 'not known any more', code: 'lost' });
    await tick();
    assert.deepEqual(ended, { success: false, error: 'not known any more', code: 'lost' });
  });

  it('ends the calls that wait when the application disconnects', async () => {
    const { client, transport } = await connected();
    transport.replies.set('Invoke', { success: true, pending: true, operation: 'op2' });
    const waiting = client.invoke('slow');
    await tick();
    await client.disconnect();
    assert.equal((await waiting).code, 'disconnected');
    assert.equal(client.isConnected, false);
  });

  it('starts again from the last route when the session could not be resumed', async () => {
    const { client, transport } = await connected();
    client.subscribeTo('history', () => {});
    transport.server.navigated(page('/cats'));
    transport.server.notified({ id: 1, title: 'T', message: 'M', type: 'Information', expirationSeconds: null });
    transport.replies.set('Invoke', { success: true, pending: true, operation: 'op9' });
    const waiting = client.invoke('slow');
    await tick();
    const lost: (string | null)[] = [];
    client.onSessionLost(r => lost.push(r.lastRoute));

    transport.changeConnection(false);
    transport.welcome = { success: true, protocol: PROTOCOL, session: 's2', resumed: false };
    transport.changeConnection(true);
    await tick();

    assert.deepEqual(lost, ['/cats']);
    assert.equal((await waiting).code, 'session-lost');
    assert.equal(client.page, null);
    assert.equal(client.toasts.length, 0);
    assert.deepEqual(transport.called('Watch'), [['history'], ['history']]);
    assert.deepEqual(transport.called('GoTo'), [['/cats']]);
  });

  it('lets the application recover by itself', async () => {
    const recovered: (string | null)[] = [];
    const { client, transport } = await connected({ recover: r => { recovered.push(r.lastRoute); } });
    transport.server.navigated(page('/dogs'));

    transport.changeConnection(false);
    transport.welcome = { success: true, protocol: PROTOCOL, session: 's2', resumed: false };
    transport.changeConnection(true);
    await tick();

    assert.deepEqual(recovered, ['/dogs']);
    assert.deepEqual(transport.called('GoTo'), []);
    assert.equal(client.isConnected, true);
  });

  it('tells the failures of the server', async () => {
    const { client, transport } = await connected();
    const failures: string[] = [];
    client.onFailure(f => failures.push(f.route + ':' + f.message));

    transport.server.failed({ route: '/x', message: 'No such page' });

    assert.deepEqual(failures, ['/x:No such page']);
  });
});
