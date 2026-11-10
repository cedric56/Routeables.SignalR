import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { describe, it } from 'node:test';
import { fileURLToPath } from 'node:url';
import {
  AckStatus, DocumentSet, DocumentState, MemoryReplicaStorage, REMOTE_PROTOCOL, RemoteErrorCodes, Replica, compareHlc,
} from '../src/index.js';
import type {
  Change, ChangeAck, DocumentId, DocumentSnapshot, LogEntry, PullRequest, PullResult, PushRequest, PushResult, RemoteHello, RemoteWelcome,
  ReplicationChannel, SnapshotRequest, SnapshotResult,
} from '../src/index.js';

const here = dirname(fileURLToPath(import.meta.url));
// dist-test/test → clients/fixtures
const fixture = JSON.parse(readFileSync(join(here, '../../../fixtures/remote-v1.json'), 'utf8'));

/** What a document shows, as the .NET fixture writes it. */
function fingerprint(state: DocumentState): unknown {
  const sorted = <T>(names: string[], value: (name: string) => T): Record<string, T> =>
    Object.fromEntries([...names].sort((a, b) => (a < b ? -1 : a > b ? 1 : 0)).map(n => [n, value(n)]));
  return {
    collection: state.id.collection,
    key: state.id.key,
    exists: state.exists,
    registers: sorted(state.registerNames, n => state.get(n)),
    elements: sorted(state.setNames, n => state.elements(n)),
    counters: sorted(state.counterNames, n => state.counter(n)),
    texts: sorted(state.textNames, n => state.text(n)),
  };
}

const byId = (a: DocumentState, b: DocumentState): number =>
  a.id.collection !== b.id.collection ? (a.id.collection < b.id.collection ? -1 : 1) : a.id.key < b.id.key ? -1 : a.id.key > b.id.key ? 1 : 0;

describe('the shared fixture (written by the .NET implementation)', () => {
  it('merges the log into what .NET shows', () => {
    assert.equal(fixture.protocol, REMOTE_PROTOCOL);
    const set = new DocumentSet();
    for (const entry of fixture.log.entries as LogEntry[]) set.apply(entry.change);
    assert.deepEqual(set.all.sort(byId).map(fingerprint), fixture.expected);
  });

  it('reads the snapshot into what .NET shows', () => {
    const states = (fixture.snapshot.documents as DocumentSnapshot[]).map(DocumentState.fromSnapshot).sort(byId);
    assert.deepEqual(states.map(fingerprint), fixture.expected);
    // and writes it back as it read it
    assert.deepEqual(states.map(s => DocumentState.fromSnapshot(s.toSnapshot())).map(fingerprint), fixture.expected);
  });

  it('orders replicas as .NET does: by their text', () => {
    const a = { wall: 1, counter: 0, replica: '0fffffff-ffff-ffff-ffff-ffffffffffff' };
    const b = { wall: 1, counter: 0, replica: 'f0000000-0000-0000-0000-000000000000' };
    assert.equal(compareHlc(a, b), -1);
  });
});

// ------------------------------------------------------------------------------------------------- a server for the tests

/** The rules of the reference server (src/Routeables.Remote), in memory, for what the client does; the real one is in tests/e2e. */
class TestServer {
  log: LogEntry[] = [];
  horizon = 0;
  readonly documents = new DocumentSet();
  readonly replicas = new Map<string, { lastSeq: number; acks: ChangeAck[] }>();
  clock = { wall: 0, counter: 0, replica: '00000000-0000-0000-0000-000000000000' };
  refuse: (change: Change) => string | null = () => null;
  now = (): number => Date.now();

  get position(): number { return this.log.length === 0 ? this.horizon : this.log[this.log.length - 1]!.position; }

  find(id: DocumentId): DocumentState | undefined { return this.documents.find(id); }

  hello(hello: RemoteHello): RemoteWelcome {
    if (hello.clock.wall <= this.now() + 300_000 && compareHlc(hello.clock, this.clock) > 0) this.clock = { ...hello.clock, replica: this.clock.replica };
    return { success: hello.protocol === REMOTE_PROTOCOL, protocol: REMOTE_PROTOCOL, clock: this.clock, lastSeq: this.replicas.get(hello.replica)?.lastSeq ?? 0, maxChangesPerPush: 3 };
  }

  push(request: PushRequest): PushResult {
    return {
      acks: request.changes.map(change => {
        const record = this.replicas.get(change.dot.replica) ?? { lastSeq: 0, acks: [] };
        this.replicas.set(change.dot.replica, record);
        if (change.dot.seq <= record.lastSeq) {
          const known = record.acks.find(a => a.dot.seq === change.dot.seq);
          return known ? { ...known, status: known.status === AckStatus.accepted ? AckStatus.duplicate : known.status } : { dot: change.dot, status: AckStatus.duplicate, position: 0 };
        }
        if (change.dot.seq !== record.lastSeq + 1) return { dot: change.dot, status: AckStatus.rejected, position: 0, code: RemoteErrorCodes.outOfOrder };
        const code = change.stamp.wall > this.now() + 300_000 ? RemoteErrorCodes.clockSkew : this.refuse(change);
        let ack: ChangeAck;
        if (code) {
          ack = { dot: change.dot, status: AckStatus.rejected, position: 0, code, message: 'no' };
        } else {
          const entry = { position: this.position + 1, change };
          this.log.push(entry);
          this.documents.apply(change);
          ack = { dot: change.dot, status: AckStatus.accepted, position: entry.position };
        }
        record.lastSeq = change.dot.seq;
        record.acks.push(ack);
        return ack;
      }),
    };
  }

  pull(request: PullRequest): PullResult {
    if (request.since < this.horizon) return { entries: [], position: request.since, more: false, snapshotRequired: true };
    const after = this.log.filter(e => e.position > request.since);
    const page = after.slice(0, Math.min(request.max, 4));
    const position = page.length === Math.min(request.max, 4) ? page[page.length - 1]!.position : this.position;
    const entries = page
      .map(e => ({ ...e, change: { ...e.change, ops: e.change.ops.filter(o => request.collections.includes(o.document.collection)) } }))
      .filter(e => e.change.ops.length > 0);
    return { entries, position, more: position < this.position, snapshotRequired: false };
  }

  snapshot(request: SnapshotRequest): SnapshotResult {
    return { documents: this.documents.all.filter(d => request.collections.includes(d.id.collection)).map(d => d.toSnapshot()), position: this.position };
  }

  compact(): void {
    this.horizon = this.position;
    this.log = [];
  }
}

/** A network that fails: down, or losing an answer after the server did the work. Messages cross it as JSON. */
class Network implements ReplicationChannel {
  down = false;
  losePushAnswer = false;
  losePullAnswer = false;
  constructor(private readonly server: TestServer) {}
  private wire<T>(value: T): T {
    if (this.down) throw new Error('No network.');
    return JSON.parse(JSON.stringify(value)) as T;
  }
  async hello(hello: RemoteHello): Promise<RemoteWelcome> { return this.wire(this.server.hello(this.wire(hello))); }
  async push(request: PushRequest): Promise<PushResult> {
    const result = this.server.push(this.wire(request));
    if (this.losePushAnswer) { this.losePushAnswer = false; throw new Error('Reset.'); }
    return this.wire(result);
  }
  async pull(request: PullRequest): Promise<PullResult> {
    const result = this.server.pull(this.wire(request));
    if (this.losePullAnswer) { this.losePullAnswer = false; throw new Error('Reset.'); }
    return this.wire(result);
  }
  async snapshot(request: SnapshotRequest): Promise<SnapshotResult> { return this.wire(this.server.snapshot(this.wire(request))); }
}

class Device {
  readonly storage = new MemoryReplicaStorage();
  network: Network;
  replica!: Replica;
  constructor(server: TestServer, readonly collections: string[], readonly now: () => number = Date.now) { this.network = new Network(server); }
  async start(): Promise<this> { this.replica = await Replica.open(this.storage, this.collections, { now: this.now }); return this; }
  async restart(): Promise<void> { this.replica = await Replica.open(this.storage, this.collections, { now: this.now }); }
  sync() { return this.replica.sync(this.network); }
  async trySync(): Promise<boolean> { try { await this.sync(); return true; } catch { return false; } }
}

const ana: DocumentId = { collection: 'Person', key: 'ana' };
const note: DocumentId = { collection: 'Note', key: 'n1' };

async function devices(count: number, collections = ['Person', 'Note']) {
  const server = new TestServer();
  const list: Device[] = [];
  for (let i = 0; i < count; i++) list.push(await new Device(server, collections).start());
  return { server, list };
}

describe('a replica', () => {
  it('writes offline at once, and two devices converge (an addition survives a concurrent removal)', async () => {
    const { server, list: [a, b] } = await devices(2);
    await a!.replica.edit().create(ana).set(ana, 'name', '"Ana"').addElement(ana, 'tags', '"x"').commit();
    await a!.sync();
    await b!.sync();
    a!.network.down = b!.network.down = true;
    await a!.replica.edit().removeElement(ana, 'tags', '"x"').increment(ana, 'visits', 2).commit();
    await b!.replica.edit().addElement(ana, 'tags', '"x"').increment(ana, 'visits', 3).commit();
    assert.deepEqual(a!.replica.find(ana)!.elements('tags'), []);
    assert.equal(await a!.trySync(), false);
    a!.network.down = b!.network.down = false;
    for (const d of [a!, b!, a!]) await d.sync();
    assert.deepEqual(server.find(ana)!.elements('tags'), ['"x"']);
    assert.equal(server.find(ana)!.counter('visits'), 5);
    for (const d of [a!, b!]) assert.deepEqual(fingerprint(d.replica.find(ana)!), fingerprint(server.find(ana)!));
    assert.equal(a!.replica.pendingCount, 0);
  });

  it('is not applied twice when an answer is lost, and survives a restart in the middle', async () => {
    const { server, list: [a] } = await devices(1);
    await a!.replica.edit().create(ana).increment(ana, 'visits', 1).commit();
    a!.network.losePushAnswer = true;
    assert.equal(await a!.trySync(), false);
    await a!.restart();
    a!.network.losePullAnswer = true;
    assert.equal(await a!.trySync(), false);
    await a!.restart();
    await a!.sync();
    assert.equal(server.log.length, 1);
    assert.equal(a!.replica.find(ana)!.counter('visits'), 1);
    assert.equal(a!.replica.pendingCount, 0);
  });

  it('drops a refused change whole, and keeps it for the application', async () => {
    const { server, list: [a] } = await devices(1);
    server.refuse = c => (c.ops.some(o => o.op.op === 'set' && o.op.value === '"forbidden"') ? RemoteErrorCodes.invalid : null);
    await a!.replica.edit().create(ana).set(ana, 'name', '"Ana"').commit();
    await a!.replica.edit().set(ana, 'name', '"forbidden"').increment(ana, 'visits', 1).commit();
    const report = await a!.sync();
    assert.equal(report.rejected, 1);
    assert.equal(a!.replica.find(ana)!.get('name'), '"Ana"');
    assert.equal(a!.replica.find(ana)!.counter('visits'), 0);
    assert.equal(a!.replica.rejected[0]!.code, RemoteErrorCodes.invalid);
    await a!.replica.dismiss(a!.replica.rejected[0]!);
    assert.equal(a!.replica.rejected.length, 0);
  });

  it('takes a snapshot below the horizon and counts its own accepted change once', async () => {
    const { server, list: [a, b] } = await devices(2);
    await b!.replica.edit().create(ana).increment(ana, 'visits', 10).commit();
    await b!.sync();
    await a!.replica.edit().increment(ana, 'visits', 1).commit();
    a!.network.losePullAnswer = true;
    assert.equal(await a!.trySync(), false);
    for (let i = 0; i < 5; i++) { await b!.replica.edit().increment(ana, 'visits', 1).commit(); await b!.sync(); }
    server.compact();
    const report = await a!.sync();
    assert.equal(report.snapshot, true);
    assert.equal(a!.replica.find(ana)!.counter('visits'), 16);
  });

  it('stamps again a change refused for a clock in the future, text edits included', async () => {
    const server = new TestServer();
    let deviceNow = Date.now() + 3 * 86_400_000;
    const a = await new Device(server, ['Note'], () => deviceNow).start();
    await a.replica.edit().create(note).insertText(note, 'body', 0, 'abc').commit();
    await a.replica.edit().insertText(note, 'body', 3, 'd').eraseText(note, 'body', 0, 1).commit();
    deviceNow = Date.now();
    await a.sync();
    assert.equal(server.find(note)!.text('body'), 'bcd');
    assert.equal(a.replica.find(note)!.text('body'), 'bcd');
    assert.equal(a.replica.rejected.length, 0);
  });

  it('merges concurrent typing in one text field', async () => {
    const { server, list: [a, b] } = await devices(2, ['Note']);
    await a!.replica.edit().create(note).insertText(note, 'body', 0, 'The quick fox').commit();
    await a!.sync();
    await b!.sync();
    await a!.replica.edit().replaceText(note, 'body', 'The quick brown fox').commit();
    await b!.replica.edit().replaceText(note, 'body', 'The quick fox jumps').commit();
    for (const d of [a!, b!, a!]) await d.sync();
    assert.equal(server.find(note)!.text('body'), 'The quick brown fox jumps');
    assert.equal(a!.replica.find(note)!.text('body'), 'The quick brown fox jumps');
    assert.equal(b!.replica.find(note)!.text('body'), 'The quick brown fox jumps');
  });

  it('converges whatever happens: random writes, cuts, lost answers and restarts', async () => {
    for (let seed = 1; seed <= 10; seed++) {
      let s = seed;
      const random = (n: number): number => { s = (s * 1103515245 + 12345) % 2147483648; return s % n; };
      const { server, list } = await devices(3);
      const people = [0, 1, 2].map(i => ({ collection: 'Person', key: `p${i}` }));
      let commits = 0;
      for (let step = 0; step < 250; step++) {
        const d = list[random(list.length)]!;
        const p = people[random(people.length)]!;
        const text = d.replica.find(note)?.text('body') ?? '';
        const edit = d.replica.edit();
        switch (random(11)) {
          case 0: d.network.down = !d.network.down; continue;
          case 1: d.network.losePushAnswer = true; await d.trySync(); continue;
          case 2: d.network.losePullAnswer = true; await d.trySync(); continue;
          case 3: await d.trySync(); continue;
          case 4: await d.restart(); continue;
          case 5: edit.increment(p, 'n', 1); break;
          case 6: edit.addElement(p, 'tags', `t${random(3)}`); break;
          case 7: edit.removeElement(p, 'tags', `t${random(3)}`); break;
          case 8: edit.create(p).set(p, 'name', `"${random(100)}"`); break;
          case 9: if (text.length > 0) { const at = random(text.length); edit.eraseText(note, 'body', at, Math.min(1 + random(2), text.length - at)); } else edit.delete(p); break;
          default: edit.insertText(note, 'body', random(text.length + 1), String.fromCharCode(97 + random(26))); break;
        }
        await edit.commit();
        commits++;
      }
      for (const d of list) { d.network.down = false; d.network.losePushAnswer = d.network.losePullAnswer = false; }
      for (let round = 0; round < 2; round++) for (const d of list) await d.sync();
      for (const id of [...people, note]) {
        const expected = server.find(id) ? fingerprint(server.find(id)!) : undefined;
        for (const d of list) assert.deepEqual(d.replica.find(id) ? fingerprint(d.replica.find(id)!) : undefined, expected, `seed ${seed}, ${id.key}`);
      }
      assert.equal(server.log.length, commits, `seed ${seed}: every change in the log once`);
      for (const d of list) { assert.equal(d.replica.pendingCount, 0); assert.equal(d.replica.rejected.length, 0); }
    }
  });
});
