// The Routeables Remote Protocol (PROTOCOL-REMOTE.md), client side: documents replicated with a server, written offline at
// once, merged the same way as the .NET implementation (src/Routeables.Remote) merges them, synchronized when the network
// is back. The fixture clients/fixtures/remote-v1.json checks that both merge alike.

/** The version of the messages: a server of another version refuses the client. */
export const REMOTE_PROTOCOL = 1;

// ---------------------------------------------------------------------------------------------------------------- identities

/** A replica: the text of a GUID, in lower case. Replicas are ordered as their texts, ordinally. */
export type ReplicaId = string;

export function newReplicaId(): ReplicaId {
  return globalThis.crypto.randomUUID().toLowerCase();
}

/** A change: its replica and its rank among the changes of that replica (1, 2, 3...). */
export interface Dot { replica: ReplicaId; seq: number }

/** A hybrid logical clock value: milliseconds, a counter, the replica that breaks the remaining ties. */
export interface Hlc { wall: number; counter: number; replica: ReplicaId }

/** The stamp of one operation: the stamp of its change, then its rank in the change. */
export interface OpStamp { stamp: Hlc; index: number }

function compareText(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}

export function compareHlc(a: Hlc, b: Hlc): number {
  return a.wall !== b.wall ? (a.wall < b.wall ? -1 : 1)
    : a.counter !== b.counter ? (a.counter < b.counter ? -1 : 1)
      : compareText(a.replica, b.replica);
}

export function compareStamps(a: OpStamp, b: OpStamp): number {
  const order = compareHlc(a.stamp, b.stamp);
  return order !== 0 ? order : a.index - b.index;
}

const sameDot = (a: Dot, b: Dot): boolean => a.seq === b.seq && a.replica === b.replica;

/** The largest counter of a stamp: an int of .NET. When it is used up, the clock moves to the next millisecond rather than wrap around. */
const MAX_STAMP_COUNTER = 2_147_483_647;

const after = (wall: number, counter: number): { wall: number; counter: number } =>
  counter >= MAX_STAMP_COUNTER ? { wall: wall + 1, counter: 0 } : { wall, counter: counter + 1 };

/** A hybrid logical clock (Kulkarni et al.): never goes back, and moves past every value it observes. */
export class HybridClock {
  private value: Hlc;

  constructor(readonly replica: ReplicaId, private readonly now: () => number = Date.now, last?: Hlc) {
    this.value = last ?? { wall: 0, counter: 0, replica };
  }

  get last(): Hlc {
    return this.value;
  }

  next(): Hlc {
    const wall = Math.max(this.now(), this.value.wall);
    this.value = wall === this.value.wall
      ? { ...after(wall, this.value.counter), replica: this.replica }
      : { wall, counter: 0, replica: this.replica };
    return this.value;
  }

  /** A value of another replica: the next values come after it. A counter out of range is never sent by a replica: not followed. */
  observe(remote: Hlc): void {
    if (!Number.isSafeInteger(remote.counter) || remote.counter < 0 || remote.counter > MAX_STAMP_COUNTER) return;
    const last = this.value;
    const wall = Math.max(this.now(), last.wall, remote.wall);
    if (wall !== last.wall && wall !== remote.wall) {
      this.value = { wall, counter: 0, replica: this.replica };
      return;
    }
    const counter = wall === last.wall && wall === remote.wall ? Math.max(last.counter, remote.counter)
      : wall === last.wall ? last.counter
        : remote.counter;
    this.value = { ...after(wall, counter), replica: this.replica };
  }

  /** Falls back from a clock that ran ahead to the later of now and `floor` (the server's): only after a clock-skew refusal. */
  fallBack(floor: Hlc): void {
    const wall = Math.max(this.now(), floor.wall);
    if (wall < this.value.wall) this.value = { wall, counter: wall === floor.wall ? floor.counter : 0, replica: this.replica };
  }
}

// ------------------------------------------------------------------------------------------------------------------ changes

export interface DocumentId { collection: string; key: string }

export interface ElementTag { dot: Dot; index: number }

/** One character of a text field: the insertion that made it, and its rank in the inserted text (UTF-16 code units). */
export interface TextId { dot: Dot; index: number; offset: number }

export interface TextSpan { dot: Dot; index: number; offset: number; length: number }

export type FieldOp =
  | { op: 'set'; field: string; value: string | null }
  | { op: 'add'; field: string; element: string }
  | { op: 'remove'; field: string; element: string; observed: ElementTag[] }
  | { op: 'increment'; field: string; delta: number }
  | { op: 'delete' }
  | { op: 'restore' }
  | { op: 'insert'; field: string; after: TextId | null; text: string }
  | { op: 'erase'; field: string; spans: TextSpan[] };

export interface DocumentOp { document: DocumentId; op: FieldOp }

/** What one replica did at once: admitted whole or refused whole. */
export interface Change { dot: Dot; stamp: Hlc; ops: DocumentOp[] }

// ----------------------------------------------------------------------------------------------------------------- messages

export interface RemoteHello { protocol: number; replica: ReplicaId; clock: Hlc }
export interface RemoteWelcome { success: boolean; protocol: number; clock: Hlc; lastSeq: number; maxChangesPerPush: number; error?: string | null }
export interface PushRequest { changes: Change[] }
export const AckStatus = { accepted: 1, rejected: 2, duplicate: 3 } as const;
export interface ChangeAck { dot: Dot; status: 1 | 2 | 3; position: number; code?: string | null; message?: string | null }
export interface PushResult { acks: ChangeAck[] }
export interface PullRequest { collections: string[]; since: number; max: number }
export interface LogEntry { position: number; change: Change }
export interface PullResult { entries: LogEntry[]; position: number; more: boolean; snapshotRequired: boolean }
export interface SnapshotRequest { collections: string[] }
export interface SnapshotResult { documents: DocumentSnapshot[]; position: number }
export interface LogAdvanced { collections: string[]; position: number }

export const RemoteErrorCodes = {
  forbidden: 'forbidden', invalid: 'invalid', tooLarge: 'too-large', clockSkew: 'clock-skew',
  replicaTaken: 'replica-taken', outOfOrder: 'out-of-order', lost: 'lost', unidentified: 'unidentified',
} as const;

/** The four calls of a replica: over SignalR (`signalRReplicationChannel`), HTTP, or in memory in a test. */
export interface ReplicationChannel {
  hello(hello: RemoteHello): Promise<RemoteWelcome>;
  push(request: PushRequest): Promise<PushResult>;
  pull(request: PullRequest): Promise<PullResult>;
  snapshot(request: SnapshotRequest): Promise<SnapshotResult>;
}

// ---------------------------------------------------------------------------------------------------------------- documents

export interface RegisterSnapshot { field: string; value: string | null; stamp: OpStamp }
export interface ElementSnapshot { field: string; element: string; tags: ElementTag[] }
export interface CounterSnapshot { field: string; value: number }
export interface TextRun { start: TextId; stamp: OpStamp; text: string; erased: boolean }
export interface TextSnapshot { field: string; runs: TextRun[] }
export interface DocumentSnapshot {
  id: DocumentId;
  exists: boolean;
  existenceStamp: OpStamp | null;
  registers: RegisterSnapshot[];
  elements: ElementSnapshot[];
  counters: CounterSnapshot[];
  texts?: TextSnapshot[] | null;
}

interface TextNode { id: TextId; value: string; stamp: OpStamp; erased: boolean }

const tagKey = (t: ElementTag): string => `${t.dot.replica}:${t.dot.seq}:${t.index}`;
const sameTextId = (a: TextId, b: TextId): boolean => a.offset === b.offset && a.index === b.index && sameDot(a.dot, b.dot);
const byOrdinal = (a: string, b: string): number => compareText(a, b);

/** The merged state of one document: what the application reads, and what later operations merge into. */
export class DocumentState {
  exists = false;
  existenceStamp: OpStamp | null = null;
  private readonly registers = new Map<string, { value: string | null; stamp: OpStamp }>();
  private readonly sets = new Map<string, Map<string, Map<string, ElementTag>>>();
  private readonly counters = new Map<string, number>();
  private readonly texts = new Map<string, TextNode[]>();

  constructor(readonly id: DocumentId) {}

  get registerNames(): string[] { return [...this.registers.keys()]; }
  /** The sets with at least one element. */
  get setNames(): string[] { return [...this.sets].filter(([, set]) => [...set.values()].some(tags => tags.size > 0)).map(([field]) => field); }
  get counterNames(): string[] { return [...this.counters.keys()]; }
  get textNames(): string[] { return [...this.texts.keys()]; }

  /** A last-writer-wins field: its value (JSON text by convention), null when never set or cleared. */
  get(field: string): string | null {
    return this.registers.get(field)?.value ?? null;
  }

  /** The elements of a set, in ordinal order. */
  elements(field: string): string[] {
    const set = this.sets.get(field);
    return set ? [...set].filter(([, tags]) => tags.size > 0).map(([element]) => element).sort(byOrdinal) : [];
  }

  tagsOf(field: string, element: string): ElementTag[] {
    return [...(this.sets.get(field)?.get(element)?.values() ?? [])];
  }

  counter(field: string): number {
    return this.counters.get(field) ?? 0;
  }

  /** The text of a text field: its characters not erased, in order. */
  text(field: string): string {
    return (this.texts.get(field) ?? []).filter(n => !n.erased).map(n => n.value).join('');
  }

  /** The characters of a text field not erased, in order: what an edit refers to. */
  textIds(field: string): TextId[] {
    return (this.texts.get(field) ?? []).filter(n => !n.erased).map(n => n.id);
  }

  apply(op: FieldOp, stamp: OpStamp, dot: Dot): void {
    switch (op.op) {
      case 'set': {
        const register = this.registers.get(op.field);
        if (!register || compareStamps(stamp, register.stamp) > 0) this.registers.set(op.field, { value: op.value, stamp });
        break;
      }
      case 'add': {
        const tag = { dot, index: stamp.index };
        this.tags(op.field, op.element).set(tagKey(tag), tag);
        break;
      }
      case 'remove': {
        const tags = this.sets.get(op.field)?.get(op.element);
        if (tags) for (const observed of op.observed) tags.delete(tagKey(observed));
        break;
      }
      case 'increment':
        this.counters.set(op.field, this.counter(op.field) + op.delta);
        break;
      case 'insert':
        this.insert(op.field, op.after, op.text, stamp, dot);
        break;
      case 'erase': {
        const nodes = this.texts.get(op.field);
        if (nodes) {
          for (const node of nodes) {
            const { dot: d, index, offset } = node.id;
            if (op.spans.some(s => sameDot(s.dot, d) && s.index === index && offset >= s.offset && offset < s.offset + s.length)) node.erased = true;
          }
        }
        break;
      }
      case 'delete':
      case 'restore':
        if (!this.existenceStamp || compareStamps(stamp, this.existenceStamp) > 0) {
          this.exists = op.op === 'restore';
          this.existenceStamp = stamp;
        }
        break;
    }
  }

  // RGA: right after the anchor, past the characters inserted there by later insertions; a missing anchor (its insertion
  // was refused, the same everywhere) means the start.
  private insert(field: string, after: TextId | null, text: string, stamp: OpStamp, dot: Dot): void {
    if (text.length === 0) return;
    let nodes = this.texts.get(field);
    if (!nodes) this.texts.set(field, nodes = []);
    const first: TextId = { dot, index: stamp.index, offset: 0 };
    if (nodes.some(n => sameTextId(n.id, first))) return;
    let at = 0;
    if (after) {
      const anchor = nodes.findIndex(n => sameTextId(n.id, after));
      at = anchor < 0 ? 0 : anchor + 1;
    }
    while (at < nodes.length) {
      const node = nodes[at]!;
      const order = compareStamps(node.stamp, stamp);
      if (order > 0 || (order === 0 && node.id.offset > 0)) at++;
      else break;
    }
    const run: TextNode[] = [];
    for (let offset = 0; offset < text.length; offset++) run.push({ id: { dot, index: stamp.index, offset }, value: text[offset]!, stamp, erased: false });
    nodes.splice(at, 0, ...run);
  }

  private tags(field: string, element: string): Map<string, ElementTag> {
    let set = this.sets.get(field);
    if (!set) this.sets.set(field, set = new Map());
    let tags = set.get(element);
    if (!tags) set.set(element, tags = new Map());
    return tags;
  }

  clone(): DocumentState {
    return DocumentState.fromSnapshot(this.toSnapshot());
  }

  toSnapshot(): DocumentSnapshot {
    const elements: ElementSnapshot[] = [];
    for (const [field, set] of this.sets)
      for (const [element, tags] of set)
        if (tags.size > 0) elements.push({ field, element, tags: [...tags.values()] });
    return {
      id: this.id,
      exists: this.exists,
      existenceStamp: this.existenceStamp,
      registers: [...this.registers].map(([field, r]) => ({ field, value: r.value, stamp: r.stamp })),
      elements,
      counters: [...this.counters].map(([field, value]) => ({ field, value })),
      texts: [...this.texts].map(([field, nodes]) => ({ field, runs: runs(nodes) })),
    };
  }

  static fromSnapshot(snapshot: DocumentSnapshot): DocumentState {
    const state = new DocumentState(snapshot.id);
    state.exists = snapshot.exists;
    state.existenceStamp = snapshot.existenceStamp ?? null;
    for (const r of snapshot.registers) state.registers.set(r.field, { value: r.value ?? null, stamp: r.stamp });
    for (const e of snapshot.elements) for (const tag of e.tags) state.tags(e.field, e.element).set(tagKey(tag), tag);
    for (const c of snapshot.counters) state.counters.set(c.field, c.value);
    for (const t of snapshot.texts ?? []) {
      const nodes: TextNode[] = [];
      for (const run of t.runs)
        for (let i = 0; i < run.text.length; i++)
          nodes.push({ id: { ...run.start, offset: run.start.offset + i }, value: run.text[i]!, stamp: run.stamp, erased: run.erased });
      state.texts.set(t.field, nodes);
    }
    return state;
  }
}

function runs(nodes: TextNode[]): TextRun[] {
  const result: TextRun[] = [];
  let i = 0;
  while (i < nodes.length) {
    const start = nodes[i]!;
    let text = start.value;
    let j = i + 1;
    while (j < nodes.length) {
      const n = nodes[j]!;
      if (!sameDot(n.id.dot, start.id.dot) || n.id.index !== start.id.index || n.id.offset !== start.id.offset + (j - i) || n.erased !== start.erased) break;
      text += n.value;
      j++;
    }
    result.push({ start: start.id, stamp: start.stamp, text, erased: start.erased });
    i = j;
  }
  return result;
}

const docKey = (id: DocumentId): string => `${id.collection}\u001f${id.key}`;

/** Documents and the changes applied to them, each once (the last seq applied per replica). */
export class DocumentSet {
  private readonly documents = new Map<string, DocumentState>();
  private readonly applied = new Map<ReplicaId, number>();

  get all(): DocumentState[] { return [...this.documents.values()]; }

  find(id: DocumentId): DocumentState | undefined {
    return this.documents.get(docKey(id));
  }

  hasApplied(dot: Dot): boolean {
    return dot.seq <= (this.applied.get(dot.replica) ?? 0);
  }

  /** Applies a change; false when it was already applied. */
  apply(change: Change): boolean {
    if (this.hasApplied(change.dot)) return false;
    this.applied.set(change.dot.replica, change.dot.seq);
    change.ops.forEach((op, index) => {
      let document = this.documents.get(docKey(op.document));
      if (!document) this.documents.set(docKey(op.document), document = new DocumentState(op.document));
      document.apply(op.op, { stamp: change.stamp, index }, change.dot);
    });
    return true;
  }

  replace(snapshots: DocumentSnapshot[], collections: Set<string>): void {
    for (const [key, document] of this.documents) if (collections.has(document.id.collection)) this.documents.delete(key);
    for (const snapshot of snapshots) this.documents.set(docKey(snapshot.id), DocumentState.fromSnapshot(snapshot));
    this.applied.clear();
  }

  load(snapshots: DocumentSnapshot[], applied: Dot[]): void {
    for (const snapshot of snapshots) this.documents.set(docKey(snapshot.id), DocumentState.fromSnapshot(snapshot));
    for (const dot of applied) this.applied.set(dot.replica, Math.max(dot.seq, this.applied.get(dot.replica) ?? 0));
  }

  get appliedDots(): Dot[] {
    return [...this.applied].map(([replica, seq]) => ({ replica, seq }));
  }
}

// ------------------------------------------------------------------------------------------------------------------ replica

/** A change the server refused: its effect is gone; kept for the application to show, correct or drop. */
export interface RejectedChange { change: Change; code: string | null; message: string | null }

/** Where a replica keeps its state between two runs: one text, written whole. */
export interface ReplicaStorage {
  read(): Promise<string | null>;
  write(state: string): Promise<void>;
}

export class MemoryReplicaStorage implements ReplicaStorage {
  private state: string | null = null;
  async read(): Promise<string | null> { return this.state; }
  async write(state: string): Promise<void> { this.state = state; }
}

/** A storage in `localStorage` (or any object with getItem/setItem) under `key`. */
export function webReplicaStorage(key: string, storage: Pick<Storage, 'getItem' | 'setItem'> = globalThis.localStorage): ReplicaStorage {
  return {
    read: async () => storage.getItem(key),
    write: async state => storage.setItem(key, state),
  };
}

export interface SyncReport { pushed: number; rejected: number; pulled: number; snapshot: boolean }

interface Pending { change: Change; position: number }

interface StoredReplica {
  format: 1;
  id: ReplicaId;
  clock: Hlc;
  nextSeq: number;
  cursor: number;
  collections: string[];
  documents: DocumentSnapshot[];
  applied: Dot[];
  pending: Pending[];
  rejected: RejectedChange[];
}

/** Accepted, but its position is not known (a duplicate whose answer the server forgot). */
const UNKNOWN = Number.MAX_SAFE_INTEGER;

export interface ReplicaOptions {
  /** The clock of the device, in milliseconds. Date.now by default. */
  now?: () => number;
}

/**
 * A replica that writes offline at once and converges with the server when it synchronizes. What the application reads
 * is the confirmed documents (from pulls) with the pending changes of this replica over them: a refused change leaves
 * exactly, and nothing has to be undone.
 */
export class Replica {
  private readonly confirmed = new DocumentSet();
  private pending: Pending[] = [];
  private rejectedChanges: RejectedChange[] = [];
  private readonly collectionSet = new Set<string>();
  private nextSeq = 1;
  private cursorValue = 0;
  private readonly listeners = new Set<(documents: DocumentId[]) => void>();
  private queue: Promise<unknown> = Promise.resolve();

  private constructor(readonly id: ReplicaId, private readonly storage: ReplicaStorage, readonly clock: HybridClock) {}

  /** Opens the replica stored in `storage`, or a new one; other collections than those stored ask a snapshot. */
  static async open(storage: ReplicaStorage, collections: string[], options: ReplicaOptions = {}): Promise<Replica> {
    const text = await storage.read();
    const stored = text ? (JSON.parse(text) as StoredReplica) : null;
    let replica: Replica;
    if (!stored) {
      const id = newReplicaId();
      replica = new Replica(id, storage, new HybridClock(id, options.now));
    } else {
      replica = new Replica(stored.id, storage, new HybridClock(stored.id, options.now, stored.clock));
      replica.confirmed.load(stored.documents, stored.applied);
      replica.pending = stored.pending;
      replica.rejectedChanges = stored.rejected;
      replica.nextSeq = stored.nextSeq;
      const same = stored.collections.length === collections.length && stored.collections.every(c => collections.includes(c));
      replica.cursorValue = same ? stored.cursor : -1;
    }
    for (const c of collections) replica.collectionSet.add(c);
    await replica.save();
    return replica;
  }

  get cursor(): number { return this.cursorValue; }
  get pendingCount(): number { return this.pending.length; }
  get rejected(): readonly RejectedChange[] { return this.rejectedChanges; }
  get collections(): string[] { return [...this.collectionSet]; }

  /** Called after local changes and after a synchronization, with the documents that may have changed. */
  onChanged(listener: (documents: DocumentId[]) => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  /** The document as the application sees it. */
  find(id: DocumentId): DocumentState | undefined {
    return this.view(id);
  }

  /** The documents of a collection, deleted ones excepted. */
  all(collection: string): DocumentState[] {
    const keys = new Map<string, DocumentId>();
    for (const d of this.confirmed.all) if (d.id.collection === collection) keys.set(docKey(d.id), d.id);
    for (const p of this.pending) for (const o of p.change.ops) if (o.document.collection === collection) keys.set(docKey(o.document), o.document);
    return [...keys.values()].map(id => this.view(id)).filter((d): d is DocumentState => !!d && d.exists);
  }

  private view(id: DocumentId): DocumentState | undefined {
    let state = this.confirmed.find(id)?.clone();
    for (const p of this.pending) {
      p.change.ops.forEach((op, index) => {
        if (docKey(op.document) !== docKey(id)) return;
        state ??= new DocumentState(id);
        state.apply(op.op, { stamp: p.change.stamp, index }, p.change.dot);
      });
    }
    return state;
  }

  /** Starts a change: operations collected, then `commit()` stores them as one change. */
  edit(): ChangeBuilder {
    return new ChangeBuilder(this);
  }

  /** @internal */
  async commit(entries: { document: DocumentId; build: (view: (id: DocumentId) => DocumentState | undefined) => FieldOp[] }[]): Promise<Change> {
    const dot: Dot = { replica: this.id, seq: this.nextSeq };
    const stamp = this.clock.next();
    const working = new Map<string, DocumentState | undefined>();
    const current = (id: DocumentId): DocumentState | undefined => {
      if (!working.has(docKey(id))) working.set(docKey(id), this.view(id));
      return working.get(docKey(id));
    };
    const ops: DocumentOp[] = [];
    for (const { document, build } of entries) {
      for (const op of build(current)) {
        let state = current(document);
        if (!state) working.set(docKey(document), state = new DocumentState(document));
        state.apply(op, { stamp, index: ops.length }, dot);
        ops.push({ document, op });
      }
    }
    if (ops.length === 0) throw new Error('The change does nothing.');
    this.nextSeq++;
    const change: Change = { dot, stamp, ops };
    this.pending.push({ change, position: 0 });
    await this.save();
    this.notify(ops.map(o => o.document));
    return change;
  }

  /**
   * Sends the pending changes, then receives what the server has after the cursor (or a snapshot when the cursor is too old).
   * Stops at the first failure of the channel, with everything stored: call it again. Calls are queued, one at a time.
   */
  sync(channel: ReplicationChannel): Promise<SyncReport> {
    const run = this.queue.then(() => this.syncNow(channel));
    this.queue = run.catch(() => undefined);
    return run;
  }

  private async syncNow(channel: ReplicationChannel): Promise<SyncReport> {
    const welcome = await channel.hello({ protocol: REMOTE_PROTOCOL, replica: this.id, clock: this.clock.last });
    if (!welcome.success) throw new Error(welcome.error ?? 'The server refused the replica.');
    this.clock.observe(welcome.clock);
    const { pushed, rejected } = await this.push(channel, welcome);
    const { pulled, snapshot } = await this.pull(channel);
    return { pushed, rejected, pulled, snapshot };
  }

  private async push(channel: ReplicationChannel, welcome: RemoteWelcome): Promise<{ pushed: number; rejected: number }> {
    let pushed = 0;
    let refused = 0;
    let fellBack = false;
    for (;;) {
      const batch = this.pending.filter(p => p.position === 0).map(p => p.change).slice(0, Math.max(1, welcome.maxChangesPerPush));
      if (batch.length === 0) return { pushed, rejected: refused };
      const result = await channel.push({ changes: batch });
      const restamp: Change[] = [];
      const touched: DocumentId[] = [];
      for (const ack of result.acks) {
        const index = this.pending.findIndex(p => sameDot(p.change.dot, ack.dot));
        if (index < 0) continue;
        pushed++;
        const entry = this.pending[index]!;
        if (ack.status === AckStatus.accepted || ack.status === AckStatus.duplicate) {
          entry.position = ack.position > 0 ? ack.position : UNKNOWN;
        } else if (ack.code === RemoteErrorCodes.clockSkew) {
          restamp.push(entry.change);
          this.pending.splice(index, 1);
        } else {
          refused++;
          this.rejectedChanges.push({ change: entry.change, code: ack.code ?? null, message: ack.message ?? null });
          this.pending.splice(index, 1);
          touched.push(...entry.change.ops.map(o => o.document));
        }
      }
      if (restamp.length > 0) this.clock.fallBack(welcome.clock);
      const renamed = new Map<string, Dot>();
      for (const change of restamp) {
        const dot: Dot = { replica: this.id, seq: this.nextSeq++ };
        renamed.set(`${change.dot.replica}:${change.dot.seq}`, dot);
        this.pending.push({ change: { ...change, dot, stamp: this.clock.next() }, position: 0 });
      }
      if (renamed.size > 0) for (const p of this.pending) p.change = rename(p.change, renamed);
      await this.save();
      if (touched.length > 0) this.notify(touched);
      if (result.acks.length < batch.length) return { pushed, rejected: refused };
      if (restamp.length > 0) {
        if (fellBack) return { pushed, rejected: refused };
        fellBack = true;
      }
    }
  }

  private async pull(channel: ReplicationChannel): Promise<{ pulled: number; snapshot: boolean }> {
    let pulled = 0;
    let snapshot = false;
    const touched = new Map<string, DocumentId>();
    const collections = this.collections;
    let since = this.cursorValue;
    for (;;) {
      const result: PullResult = since < 0
        ? { entries: [], position: since, more: false, snapshotRequired: true }
        : await channel.pull({ collections, since, max: 500 });

      if (result.snapshotRequired) {
        const full = await channel.snapshot({ collections });
        for (const d of this.confirmed.all) touched.set(docKey(d.id), d.id);
        this.confirmed.replace(full.documents, new Set(collections));
        for (const d of full.documents) touched.set(docKey(d.id), d.id);
        this.pending = this.pending.filter(p => !(p.position > 0 && p.position <= full.position));
        this.cursorValue = since = full.position;
        snapshot = true;
        await this.save();
        continue;
      }

      for (const entry of result.entries) {
        if (this.confirmed.apply(entry.change)) pulled++;
        this.pending = this.pending.filter(p => !sameDot(p.change.dot, entry.change.dot));
        for (const o of entry.change.ops) touched.set(docKey(o.document), o.document);
      }
      this.pending = this.pending.filter(p => !(p.position > 0 && p.position <= result.position));
      this.cursorValue = since = result.position;
      if (!result.more) {
        for (const lost of this.pending.filter(p => p.position === UNKNOWN)) {
          this.rejectedChanges.push({ change: lost.change, code: RemoteErrorCodes.lost, message: 'The server decided on this change before, and its answer is lost: it is not in the log.' });
          for (const o of lost.change.ops) touched.set(docKey(o.document), o.document);
        }
        this.pending = this.pending.filter(p => p.position !== UNKNOWN);
      }
      await this.save();
      if (!result.more) break;
    }
    if (touched.size > 0) this.notify([...touched.values()]);
    return { pulled, snapshot };
  }

  /** Forgets a refused change, once the application dealt with it. */
  async dismiss(rejected: RejectedChange): Promise<void> {
    this.rejectedChanges = this.rejectedChanges.filter(r => r !== rejected);
    await this.save();
  }

  private notify(documents: DocumentId[]): void {
    for (const listener of this.listeners) listener(documents);
  }

  private save(): Promise<void> {
    const stored: StoredReplica = {
      format: 1,
      id: this.id,
      clock: this.clock.last,
      nextSeq: this.nextSeq,
      cursor: this.cursorValue,
      collections: this.collections,
      documents: this.confirmed.all.map(d => d.toSnapshot()),
      applied: this.confirmed.appliedDots,
      pending: this.pending,
      rejected: this.rejectedChanges,
    };
    return this.storage.write(JSON.stringify(stored));
  }
}

function rename(change: Change, renamed: Map<string, Dot>): Change {
  const of = (dot: Dot): Dot => renamed.get(`${dot.replica}:${dot.seq}`) ?? dot;
  return {
    ...change,
    ops: change.ops.map(o => {
      const op = o.op;
      switch (op.op) {
        case 'remove': return { ...o, op: { ...op, observed: op.observed.map(t => ({ ...t, dot: of(t.dot) })) } };
        case 'insert': return op.after ? { ...o, op: { ...op, after: { ...op.after, dot: of(op.after.dot) } } } : o;
        case 'erase': return { ...o, op: { ...op, spans: op.spans.map(s => ({ ...s, dot: of(s.dot) })) } };
        default: return o;
      }
    }),
  };
}

function spans(ids: TextId[]): TextSpan[] {
  const result: TextSpan[] = [];
  for (const id of ids) {
    const last = result[result.length - 1];
    if (last && sameDot(last.dot, id.dot) && last.index === id.index && last.offset + last.length === id.offset) last.length++;
    else result.push({ dot: id.dot, index: id.index, offset: id.offset, length: 1 });
  }
  return result;
}

const isHigh = (c: number): boolean => c >= 0xd800 && c <= 0xdbff;
const isLow = (c: number): boolean => c >= 0xdc00 && c <= 0xdfff;

/** The smallest edit from the text of a field to `text`: the common start and end are kept. */
function diff(state: DocumentState | undefined, field: string, text: string): FieldOp[] {
  const current = state?.text(field) ?? '';
  let start = 0;
  while (start < current.length && start < text.length && current[start] === text[start]) start++;
  if (start > 0 && isHigh(current.charCodeAt(start - 1))) start--;
  let end = 0;
  while (end < current.length - start && end < text.length - start && current[current.length - 1 - end] === text[text.length - 1 - end]) end++;
  if (end > 0 && isLow(current.charCodeAt(current.length - end))) end--;
  const ids = state?.textIds(field) ?? [];
  const erased = current.length - start - end;
  const inserted = text.substring(start, text.length - end);
  const ops: FieldOp[] = [];
  if (erased > 0) ops.push({ op: 'erase', field, spans: spans(ids.slice(start, start + erased)) });
  if (inserted.length > 0) ops.push({ op: 'insert', field, after: start === 0 ? null : ids[start - 1]!, text: inserted });
  return ops;
}

type View = (id: DocumentId) => DocumentState | undefined;

/** The operations of one change, built at commit from what the replica sees, each seeing the ones before it. */
export class ChangeBuilder {
  private readonly entries: { document: DocumentId; build: (view: View) => FieldOp[] }[] = [];

  constructor(private readonly replica: Replica) {}

  private add(document: DocumentId, build: (view: View) => FieldOp[]): this {
    this.entries.push({ document, build });
    return this;
  }

  get count(): number { return this.entries.length; }

  /** Creates the document (or restores it after a deletion). */
  create(document: DocumentId): this { return this.add(document, () => [{ op: 'restore' }]); }
  delete(document: DocumentId): this { return this.add(document, () => [{ op: 'delete' }]); }
  /** A last-writer-wins field; the value is opaque text (JSON by convention), null clears it. */
  set(document: DocumentId, field: string, value: string | null): this { return this.add(document, () => [{ op: 'set', field, value }]); }
  addElement(document: DocumentId, field: string, element: string): this { return this.add(document, () => [{ op: 'add', field, element }]); }
  /** Removes the element as this replica sees it: an addition made elsewhere and not seen yet survives. */
  removeElement(document: DocumentId, field: string, element: string): this {
    return this.add(document, view => [{ op: 'remove', field, element, observed: view(document)?.tagsOf(field, element) ?? [] }]);
  }
  increment(document: DocumentId, field: string, delta: number): this {
    if (!Number.isSafeInteger(delta)) throw new RangeError('A counter moves by a whole number.');
    return this.add(document, () => [{ op: 'increment', field, delta }]);
  }

  /** Inserts `text` at `position` (UTF-16 code units) of the text this replica sees. */
  insertText(document: DocumentId, field: string, position: number, text: string): this {
    return this.add(document, view => {
      const ids = view(document)?.textIds(field) ?? [];
      if (position < 0 || position > ids.length) throw new RangeError(`The text has ${ids.length} characters.`);
      return text.length === 0 ? [] : [{ op: 'insert', field, after: position === 0 ? null : ids[position - 1]!, text }];
    });
  }

  eraseText(document: DocumentId, field: string, position: number, length: number): this {
    return this.add(document, view => {
      const ids = view(document)?.textIds(field) ?? [];
      if (position < 0 || length < 0 || position + length > ids.length) throw new RangeError(`The text has ${ids.length} characters.`);
      return length === 0 ? [] : [{ op: 'erase', field, spans: spans(ids.slice(position, position + length)) }];
    });
  }

  /** Gives a text field a new value with the smallest edit, so that what others change elsewhere in it survives. */
  replaceText(document: DocumentId, field: string, text: string): this {
    return this.add(document, view => diff(view(document), field, text));
  }

  commit(): Promise<Change> {
    if (this.entries.length === 0) return Promise.reject(new Error('A change has at least one operation.'));
    return this.replica.commit(this.entries);
  }
}
