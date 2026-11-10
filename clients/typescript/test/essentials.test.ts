import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import {
  ESSENTIALS_PROTOCOL, EssentialsBridge, EssentialsError, PROTOCOL, RouteablesClient, essentialsErrorCode, preferencesService,
} from '../src/index.js';
import type { Ack, EssentialsServices, PreferenceStorage, RouteableTransport, ServerMessages, ServerMethod, Welcome } from '../src/index.js';

/** A Storage in memory, as localStorage behaves. */
class MemoryStorage implements PreferenceStorage {
  private readonly items = new Map<string, string>();
  get length(): number { return this.items.size; }
  key(index: number): string | null { return [...this.items.keys()][index] ?? null; }
  getItem(key: string): string | null { return this.items.get(key) ?? null; }
  setItem(key: string, value: string): void { this.items.set(key, value); }
  removeItem(key: string): void { this.items.delete(key); }
}

/** Records what the bridge sends. */
function recorder(): { calls: { method: ServerMethod; args: unknown[] }[]; call: (method: ServerMethod, ...args: unknown[]) => Promise<unknown> } {
  const calls: { method: ServerMethod; args: unknown[] }[] = [];
  return {
    calls,
    call: async (method, ...args) => {
      calls.push({ method, args });
      return { success: true };
    },
  };
}

const tick = () => new Promise(resolve => setTimeout(resolve, 0));

describe('the Essentials bridge', () => {
  it('announces its services and their state', async () => {
    const storage = new MemoryStorage();
    storage.setItem('routeables:prefs::theme', 'dark');
    storage.setItem('routeables:prefs:settings:volume', '7');
    storage.setItem('something:else', 'x');
    const { calls, call } = recorder();
    const bridge = new EssentialsBridge({ preferences: preferencesService(storage) }, call);

    await bridge.ready();

    assert.equal(calls[0]!.method, 'EssentialsReady');
    assert.deepEqual(calls[0]!.args[0], {
      version: ESSENTIALS_PROTOCOL,
      capabilities: ['preferences'],
      state: { preferences: { '': { theme: 'dark' }, settings: { volume: '7' } } },
    });
  });

  it('runs a method the service lists and answers with its result', async () => {
    const storage = new MemoryStorage();
    const { calls, call } = recorder();
    const bridge = new EssentialsBridge({ preferences: preferencesService(storage) }, call);

    await bridge.invoke({ id: 'e1', service: 'preferences', method: 'set', args: ['', 'theme', 'light'] });

    assert.equal(storage.getItem('routeables:prefs::theme'), 'light');
    assert.deepEqual(calls[0], { method: 'EssentialsResult', args: [{ id: 'e1', success: true, result: null }] });
  });

  it('never runs what the service does not list', async () => {
    const { calls, call } = recorder();
    const services: EssentialsServices = { preferences: preferencesService(new MemoryStorage()) };
    const bridge = new EssentialsBridge(services, call);

    for (const [service, method] of [
      ['preferences', 'constructor'], ['preferences', '__proto__'], ['preferences', 'toString'], ['preferences', 'hasOwnProperty'],
      ['constructor', 'call'], ['__proto__', 'set'], ['geolocation', 'getLocation'],
    ] as const)
      await bridge.invoke({ id: `${service}.${method}`, service, method, args: [] });

    assert.equal(calls.length, 7);
    for (const { args } of calls) {
      const answer = args[0] as { success: boolean; code: string };
      assert.equal(answer.success, false);
      assert.equal(answer.code, 'not-supported');
    }
  });

  it('reports the failures with the codes of the server', async () => {
    const { calls, call } = recorder();
    const bridge = new EssentialsBridge({
      camera: {
        methods: {
          denied: () => { throw Object.assign(new Error('no'), { name: 'NotAllowedError' }); },
          rejected: () => Promise.reject(new EssentialsError('off', 'not-enabled')),
        },
      },
      preferences: preferencesService(new MemoryStorage()),
    }, call);

    await bridge.invoke({ id: 'a', service: 'camera', method: 'denied', args: [] });
    await bridge.invoke({ id: 'b', service: 'camera', method: 'rejected', args: [] });
    await bridge.invoke({ id: 'c', service: 'preferences', method: 'set', args: ['', 42, 'x'] });

    assert.deepEqual(calls.map(c => (c.args[0] as { code: string }).code), ['permission-denied', 'not-enabled', 'invalid-argument']);
    assert.equal(essentialsErrorCode(new Error('plain')), 'failed');
  });

  it('starts and stops the events the server asks for, and only those', async () => {
    const { calls, call } = recorder();
    let emit: ((payload: unknown) => void) | null = null;
    let stopped = 0;
    const bridge = new EssentialsBridge({
      connectivity: { events: { changed: e => { emit = e; return () => { stopped++; }; } } },
    }, call);

    bridge.subscribe({ service: 'connectivity', event: 'unknown', active: true });
    bridge.subscribe({ service: 'connectivity', event: 'changed', active: true });
    bridge.subscribe({ service: 'connectivity', event: 'changed', active: true });   // asked again: one listener
    emit!({ networkAccess: 'None', profiles: [] });
    await tick();
    bridge.subscribe({ service: 'connectivity', event: 'changed', active: false });

    assert.deepEqual(calls, [{ method: 'EssentialsEvent', args: [{ service: 'connectivity', event: 'changed', payload: { networkAccess: 'None', profiles: [] } }] }]);
    assert.equal(stopped, 1);
  });

  it('stops its events when it announces itself again: the server asks for the ones it still wants', async () => {
    const { call } = recorder();
    let stopped = 0;
    const bridge = new EssentialsBridge({ connectivity: { events: { changed: () => () => { stopped++; } } } }, call);
    bridge.subscribe({ service: 'connectivity', event: 'changed', active: true });

    await bridge.ready();

    assert.equal(stopped, 1);
  });
});

describe('the preferences of the browser', () => {
  it('clears one container only', () => {
    const storage = new MemoryStorage();
    const preferences = preferencesService(storage);
    const methods = preferences.methods as Record<string, (...args: unknown[]) => void>;
    methods['set']!('a', 'x', '1');
    methods['set']!('a:b', 'y', '2');   // a shared name with the separator in it
    methods['set']!('', 'z', '3');

    methods['clear']!('a');

    assert.deepEqual(preferences.state!(), { 'a:b': { y: '2' }, '': { z: '3' } });
  });
});

/** A transport whose server answers Hello and records the rest. */
class EssentialsTransport implements RouteableTransport {
  readonly calls: { method: ServerMethod; args: unknown[] }[] = [];
  server!: ServerMessages;
  private connection: (connected: boolean) => void = () => {};

  async start(handlers: ServerMessages): Promise<void> { this.server = handlers; }
  async stop(): Promise<void> {}
  async call<T>(method: ServerMethod, ...args: unknown[]): Promise<T> {
    this.calls.push({ method, args });
    if (method === 'Hello') return { success: true, protocol: PROTOCOL, session: 's1', resumed: true } satisfies Welcome as T;
    return { success: true } satisfies Ack as T;
  }
  onConnectionChange(handler: (connected: boolean) => void): void { this.connection = handler; }
  changeConnection(connected: boolean): void { this.connection(connected); }
}

describe('a client with Essentials', () => {
  it('announces them right after each Hello, before it is connected', async () => {
    const transport = new EssentialsTransport();
    const client = new RouteablesClient(transport, { essentials: { preferences: preferencesService(new MemoryStorage()) } });
    const seen: boolean[] = [];
    client.onConnection(connected => seen.push(connected));

    await client.connect();
    assert.deepEqual(transport.calls.map(c => c.method), ['Hello', 'EssentialsReady']);
    assert.deepEqual(seen, [true]);

    transport.changeConnection(false);
    transport.changeConnection(true);
    await tick();
    await tick();
    assert.deepEqual(transport.calls.map(c => c.method), ['Hello', 'EssentialsReady', 'Hello', 'EssentialsReady']);
  });

  it('answers the calls of the server', async () => {
    const transport = new EssentialsTransport();
    const storage = new MemoryStorage();
    const client = new RouteablesClient(transport, { essentials: { preferences: preferencesService(storage) } });
    await client.connect();

    transport.server.essentialsInvoke!({ id: 'e1', service: 'preferences', method: 'set', args: ['', 'k', 'v'] });
    await tick();

    assert.equal(storage.getItem('routeables:prefs::k'), 'v');
    assert.deepEqual(transport.calls.at(-1), { method: 'EssentialsResult', args: [{ id: 'e1', success: true, result: null }] });
  });

  it('says nothing of Essentials without them', async () => {
    const transport = new EssentialsTransport();
    const client = new RouteablesClient(transport);
    await client.connect();

    assert.deepEqual(transport.calls.map(c => c.method), ['Hello']);
    assert.equal(transport.server.essentialsInvoke, undefined);
  });
});
