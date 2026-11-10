import { EssentialsBridge } from './essentials.js';
import type { EssentialsServices } from './essentials.js';
import { MODAL, PAGE, PROTOCOL } from './types.js';
import type {
  Ack, ChangedMessage, ClosedMessage, CompletedMessage, Dialog, FailureMessage, NavigatedMessage, Page, Patch, RouteableTransport,
  ServerMethod, Toast, Welcome,
} from './types.js';

export type PageListener = (page: Page | null) => void;
export type FailureListener = (failure: FailureMessage) => void;
export type ConnectionListener = (connected: boolean) => void;
export type DialogListener = (dialogs: readonly Dialog[]) => void;
export type ToastListener = (toasts: readonly Toast[]) => void;

/** A call of the client that failed without anybody waiting for its answer (a watch), or that did not reach the server. */
export interface ClientError {
  readonly method: ServerMethod;
  readonly target?: string;
  readonly message: string;
  readonly code?: string | null;
}
export type ErrorListener = (error: ClientError) => void;

/** What the recovery of a lost session knows. */
export interface Recovery {
  readonly client: RouteablesClient;
  /** The route the main page showed before the session was lost, or null. */
  readonly lastRoute: string | null;
}

export interface RouteablesClientOptions {
  /**
   * What to do when the server could not resume the session after a reconnection (it was gone: the grace period ended, the server restarted):
   * the view models are new, nothing is shown. By default the main router goes back to the route it showed; `false` does nothing (the
   * application listens to `onSessionLost`).
   */
  recover?: ((recovery: Recovery) => void | Promise<void>) | false;
  /** Whether a notification that has an expiration is closed by the client after it (`dismiss`). `true` by default. */
  expireToasts?: boolean;
  /**
   * The device APIs the server may use for this user (the `IPreferences`, `IConnectivity`… of the view models, with Routeables.SignalR.Essentials):
   * `browserEssentials()` for the browser ones, or your own. They are announced after each `Hello`; without them, the server sees no Essentials.
   */
  essentials?: EssentialsServices;
}

const SESSION_LOST: Ack = { success: false, error: 'The session was lost: the call may not have ended.', code: 'session-lost' };
const DISCONNECTED: Ack = { success: false, error: 'The connection was closed: the call may not have ended.', code: 'disconnected' };

/**
 * Holds what the server shows, and sends what the user does. It does not know Angular or Vue: a component subscribes
 * and shows `page.state`; a framework layer turns the subscription into signals, observables or refs.
 *
 * What the server shows is a set of targets: the page of the main router (""), the modal ("$modal"), the regions (by their
 * name) and the view models the application registered ("$name"). The page and the modal are always sent; the others
 * are sent while somebody watches them (`watch`).
 *
 * Every target is an ordered stream: a page (a generation), then its changes (versions 1, 2, …). A change of an older page is dropped,
 * and a missing change makes the client ask the state again. After a reconnection the client resumes its session on the server: the view
 * models, their state and the dialogs that wait come back; when it cannot, `recover` runs.
 */
export class RouteablesClient {
  private readonly pages = new Map<string, Page | null>();
  private readonly pageListeners = new Map<string, Set<PageListener>>();
  private readonly watchers = new Map<string, number>();
  private readonly failureListeners = new Set<FailureListener>();
  private readonly connectionListeners = new Set<ConnectionListener>();
  private readonly dialogListeners = new Set<DialogListener>();
  private readonly toastListeners = new Set<ToastListener>();
  private readonly errorListeners = new Set<ErrorListener>();
  private readonly sessionLostListeners = new Set<(recovery: Recovery) => void>();
  private readonly instances = new Map<string, number>();
  private readonly resyncing = new Set<string>();
  private readonly operations = new Map<string, (ack: Ack) => void>();
  private readonly early = new Map<string, Ack>();
  private dialogList: readonly Dialog[] = [];
  private toastList: readonly Toast[] = [];
  private connected = false;
  private sequence = 0;
  private session: string | null = null;
  private lastRoute: string | null = null;
  private readonly essentials: EssentialsBridge | null;

  constructor(private readonly transport: RouteableTransport, private readonly options: RouteablesClientOptions = {}) {
    this.essentials = options.essentials ? new EssentialsBridge(options.essentials, (method, ...args) => this.transport.call(method, ...args)) : null;
    transport.onConnectionChange(connected => {
      if (!connected) {
        // The pages and the calls that wait stay: the session is resumed when the connection comes back (by itself, or by connect() once
        // SignalR stopped retrying), and the calls end then; or it is recovered, and they end as lost.
        this.setConnected(false);
        return;
      }
      void this.hello().catch(error => this.fail('Hello', undefined, error));
    });
  }

  /** The page of the main router, or null before the first one. */
  get page(): Page | null {
    return this.pageOf(PAGE);
  }

  /** Whether the client is connected and has its session. */
  get isConnected(): boolean {
    return this.connected;
  }

  /** What a target shows now. */
  pageOf(target: string): Page | null {
    return this.pages.get(target) ?? null;
  }

  /** The dialogs that wait for an answer. */
  get dialogs(): readonly Dialog[] {
    return this.dialogList;
  }

  /** The notifications that are shown. */
  get toasts(): readonly Toast[] {
    return this.toastList;
  }

  /** Opens the connection and says hello (a session that dropped is resumed). Go to a route afterwards. */
  async connect(): Promise<void> {
    await this.transport.start({
      navigated: message => this.onNavigated(message),
      changed: message => this.onChanged(message),
      failed: message => this.failureListeners.forEach(listener => listener(message)),
      dialogRequested: dialog => this.onDialog(dialog),
      notified: toast => this.onToast(toast),
      closed: message => this.onClosed(message),
      completed: message => this.onCompleted(message),
      ...(this.essentials === null ? {} : {
        essentialsInvoke: message => void this.essentials!.invoke(message),
        essentialsSubscribe: message => this.essentials!.subscribe(message),
      }),
    });
    await this.hello();
  }

  /** Closes the connection: the application is done with it. The calls that wait end with `{ code: 'disconnected' }`; the pages stay. */
  async disconnect(): Promise<void> {
    this.essentials?.stopAll();
    await this.transport.stop();
    this.setConnected(false);
    this.abandonCalls(DISCONNECTED);
  }

  // The calls that wait will not hear of their end: they end with the given failure.
  private abandonCalls(ack: Ack): void {
    const waiting = [...this.operations.values()];
    this.operations.clear();
    this.early.clear();
    for (const settle of waiting) settle(ack);
  }

  // ---------------------------------------------------------------- what the user does

  /** Goes to a route, as the address bar would. The page arrives through the subscription; the answer comes once the route change ended. */
  goTo(route: string): Promise<Ack> {
    return this.send('GoTo', undefined, route);
  }

  /** Goes to a route in the router of a target: a region, by its name. */
  goToIn(target: string, route: string): Promise<Ack> {
    return target === PAGE ? this.goTo(route) : this.send('GoToIn', target, target, route);
  }

  /** Calls a method of the current page, by its name. The answer comes when the command ended (after a dialog it asked, for instance). */
  invoke(command: string, ...args: unknown[]): Promise<Ack> {
    return this.send('Invoke', undefined, command, args);
  }

  /** Calls a method of what a target shows, by its name. */
  invokeOn(target: string, command: string, ...args: unknown[]): Promise<Ack> {
    return this.send('InvokeOn', target, target, command, args);
  }

  /** Calls a command of the state (a `CommandRef` found in the state of a target). */
  run(target: string, command: { readonly $command: string }, ...args: unknown[]): Promise<Ack> {
    return this.invokeOn(target, command.$command, ...args);
  }

  /** Sets a property of the current page (a text box, a check box). The server confirms by telling the change. */
  set(property: string, value: unknown): Promise<Ack> {
    return this.send('Set', undefined, property, value);
  }

  /** Sets a property of what a target shows. */
  setOn(target: string, property: string, value: unknown): Promise<Ack> {
    return this.send('SetOn', target, target, property, value);
  }

  /**
   * Answers a dialog: the index of the button the user chose. The dialog leaves the list once the server took the answer (or does not know
   * the dialog any more); it stays when the answer did not reach the server, to be answered again.
   */
  async answer(dialog: Dialog, button: number): Promise<Ack> {
    const ack = await this.send('Answer', undefined, dialog.id, button);
    if (ack.success || ack.code === 'not-found') this.setDialogs(this.dialogList.filter(d => d.id !== dialog.id));
    return ack;
  }

  /** Closes a notification, or tells that the user clicked it. It leaves the list once the server took it. */
  async dismiss(toast: Toast, click = false): Promise<Ack> {
    const ack = await this.send('Dismiss', undefined, toast.id, click);
    if (ack.success || ack.code === 'not-found') this.setToasts(this.toastList.filter(t => t.id !== toast.id));
    return ack;
  }

  // ---------------------------------------------------------------- what the user sees

  /** Calls <paramref name="listener"/> now and at every change of the main page or its state. Returns the way to stop. */
  subscribe(listener: PageListener): () => void {
    return this.subscribeTo(PAGE, listener);
  }

  /**
   * Calls <paramref name="listener"/> now and at every change of what a target shows. A region or a registered view model
   * is sent by the server while somebody subscribes. Returns the way to stop.
   */
  subscribeTo(target: string, listener: PageListener): () => void {
    let listeners = this.pageListeners.get(target);
    if (listeners === undefined) this.pageListeners.set(target, (listeners = new Set()));
    listeners.add(listener);
    this.watch(target);
    listener(this.pageOf(target));
    let subscribed = true;
    return () => {
      if (!subscribed) return;
      subscribed = false;
      listeners.delete(listener);
      this.unwatch(target);
    };
  }

  subscribeDialogs(listener: DialogListener): () => void {
    this.dialogListeners.add(listener);
    listener(this.dialogList);
    return () => this.dialogListeners.delete(listener);
  }

  subscribeToasts(listener: ToastListener): () => void {
    this.toastListeners.add(listener);
    listener(this.toastList);
    return () => this.toastListeners.delete(listener);
  }

  /** What failed on the server without a call waiting for it: a route change, a part of a state that could not be sent. */
  onFailure(listener: FailureListener): () => void {
    this.failureListeners.add(listener);
    return () => this.failureListeners.delete(listener);
  }

  /** A call that did not reach the server, or a watch the server refused. */
  onError(listener: ErrorListener): () => void {
    this.errorListeners.add(listener);
    return () => this.errorListeners.delete(listener);
  }

  /** Whether the client is connected (with its session): false when the connection drops, true when it comes back. */
  onConnection(listener: ConnectionListener): () => void {
    this.connectionListeners.add(listener);
    return () => this.connectionListeners.delete(listener);
  }

  /** The server could not resume the session after a reconnection: the view models are new. Called before `recover`. */
  onSessionLost(listener: (recovery: Recovery) => void): () => void {
    this.sessionLostListeners.add(listener);
    return () => this.sessionLostListeners.delete(listener);
  }

  // ---------------------------------------------------------------- session

  private async hello(): Promise<void> {
    const previous = this.session;
    // The calls that go on are named: a resumed session tells again the end of those that ended while the connection was gone.
    const welcome = await this.transport.call<Welcome>('Hello', { protocol: PROTOCOL, resume: previous, waiting: [...this.operations.keys()] });
    if (!welcome.success) {
      const message = welcome.error ?? `The server speaks the protocol ${welcome.protocol}, this client ${PROTOCOL}.`;
      this.fail('Hello', undefined, new Error(message), 'protocol');
      throw new Error(message);
    }
    this.session = welcome.session;

    if (previous !== null && !welcome.resumed) {
      // A new session: what was shown belongs to view models that are gone.
      for (const target of [...this.pages.keys()]) this.setPage(target, null);
      this.instances.clear();
      this.setDialogs([]);
      this.setToasts([]);
      this.abandonCalls(SESSION_LOST);
    }

    // Before anything is shown or navigated: the view models read the preferences and the network of this device on the server.
    await this.announceEssentials();

    this.setConnected(true);
    // A resumed session already sent what it watches; a new one watches nothing yet.
    if (!welcome.resumed) for (const target of this.watchers.keys()) this.watchRemote(target);

    if (previous !== null && !welcome.resumed) {
      const recovery: Recovery = { client: this, lastRoute: this.lastRoute };
      this.sessionLostListeners.forEach(listener => listener(recovery));
      if (this.options.recover !== false) {
        const recover = this.options.recover ?? (async (r: Recovery) => { if (r.lastRoute !== null) await r.client.goTo(r.lastRoute); });
        await recover(recovery);
      }
    }
  }

  private async announceEssentials(): Promise<void> {
    if (this.essentials === null) return;
    try {
      const ack = await this.essentials.ready();
      if (!ack.success) this.fail('EssentialsReady', undefined, new Error(ack.error ?? 'The server refused the Essentials of this client.'), ack.code ?? 'failed');
    } catch (error) {
      this.fail('EssentialsReady', undefined, error);
    }
  }

  private setConnected(connected: boolean): void {
    if (this.connected === connected) return;
    this.connected = connected;
    this.connectionListeners.forEach(listener => listener(connected));
  }

  // Sends a call and waits for its end: the answer, or Completed when the server answered that it goes on.
  private async send(method: ServerMethod, target: string | undefined, ...args: unknown[]): Promise<Ack> {
    let ack: Ack;
    try {
      ack = await this.transport.call<Ack>(method, ...args);
    } catch (error) {
      return this.fail(method, target, error);
    }
    if (!ack.pending || !ack.operation) return ack;

    const operation = ack.operation;
    const early = this.early.get(operation);
    if (early !== undefined) {
      this.early.delete(operation);
      return early;
    }
    return new Promise<Ack>(resolve => this.operations.set(operation, resolve));
  }

  private fail(method: ServerMethod, target: string | undefined, error: unknown, code = 'transport'): Ack {
    const message = error instanceof Error ? error.message : String(error);
    this.errorListeners.forEach(listener => listener({ method, target, message, code }));
    return { success: false, error: message, code };
  }

  // ---------------------------------------------------------------- watchers

  private isAlwaysSent(target: string): boolean {
    return target === PAGE || target === MODAL;
  }

  private watch(target: string): void {
    if (this.isAlwaysSent(target)) return;
    const count = (this.watchers.get(target) ?? 0) + 1;
    this.watchers.set(target, count);
    if (count === 1 && this.connected) this.watchRemote(target);
  }

  private watchRemote(target: string): void {
    void this.send('Watch', target, target).then(ack => {
      if (!ack.success && ack.code !== 'transport')
        this.errorListeners.forEach(listener => listener({ method: 'Watch', target, message: ack.error ?? 'The target cannot be watched.', code: ack.code }));
    });
  }

  private unwatch(target: string): void {
    if (this.isAlwaysSent(target)) return;
    const count = (this.watchers.get(target) ?? 0) - 1;
    if (count > 0) {
      this.watchers.set(target, count);
      return;
    }
    this.watchers.delete(target);
    this.setPage(target, null);
    if (this.connected) void this.send('Unwatch', target, target);
  }

  private resync(target: string): void {
    if (this.resyncing.has(target)) return;
    this.resyncing.add(target);
    void this.send('Resync', target, target).then(ack => {
      if (!ack.success) this.resyncing.delete(target);
    });
  }

  // ---------------------------------------------------------------- what the server tells

  private onNavigated(message: NavigatedMessage): void {
    const target = message.target ?? PAGE;
    const current = this.pageOf(target);
    if (current !== null && message.generation < current.generation) return;   // an older page

    this.resyncing.delete(target);
    // The same view model sent again (a resync, a resumed session) keeps its sequence: a component is not recreated.
    const sameInstance = current !== null && this.instances.get(target) === message.instance;
    this.instances.set(target, message.instance);
    if (target === PAGE) this.lastRoute = message.route;
    this.setPage(target, {
      sequence: sameInstance ? current!.sequence : ++this.sequence,
      target,
      route: message.route,
      viewModel: message.viewModel,
      state: message.state,
      commands: message.commands,
      generation: message.generation,
      version: 0,
    });
  }

  private onChanged(message: ChangedMessage): void {
    const target = message.target ?? PAGE;
    const page = this.pageOf(target);
    if (page === null || message.generation < page.generation) return;   // a change of a page that is gone
    if (message.generation > page.generation || message.version > page.version + 1) {
      this.resync(target);   // a page or a change was lost: the whole state is asked again
      return;
    }
    if (message.version <= page.version) return;   // already applied

    let state = page.state as Record<string, unknown>;
    for (const patch of message.patches) state = apply(state, patch);
    // A new object, so that a framework that compares references sees the change.
    this.setPage(target, { ...page, state, version: message.version });
  }

  private onClosed(message: ClosedMessage): void {
    const page = this.pageOf(message.target);
    if (page !== null && message.generation !== undefined && page.generation > message.generation) return;   // closes an older page
    this.instances.delete(message.target);
    this.setPage(message.target, null);
  }

  private onCompleted(message: CompletedMessage): void {
    const ack: Ack = { success: message.success, error: message.error, code: message.code };
    const settle = this.operations.get(message.operation);
    if (settle === undefined) {
      // Arrived before the answer that names it was read: kept for it.
      this.early.set(message.operation, ack);
      if (this.early.size > 256) this.early.delete(this.early.keys().next().value!);
      return;
    }
    this.operations.delete(message.operation);
    settle(ack);
  }

  private onDialog(dialog: Dialog): void {
    if (this.dialogList.some(d => d.id === dialog.id)) return;   // sent again by a resumed session
    this.setDialogs([...this.dialogList, dialog]);
  }

  private onToast(toast: Toast): void {
    if (this.toastList.some(t => t.id === toast.id)) return;
    this.setToasts([...this.toastList, toast]);
    // A notification closes by itself after its time.
    if (this.options.expireToasts !== false && toast.expirationSeconds !== null)
      setTimeout(() => {
        if (this.toastList.some(t => t.id === toast.id)) void this.dismiss(toast, false);
      }, toast.expirationSeconds * 1000);
  }

  private setPage(target: string, page: Page | null): void {
    this.pages.set(target, page);
    this.pageListeners.get(target)?.forEach(listener => listener(page));
  }

  private setDialogs(dialogs: readonly Dialog[]): void {
    this.dialogList = dialogs;
    this.dialogListeners.forEach(listener => listener(dialogs));
  }

  private setToasts(toasts: readonly Toast[]): void {
    this.toastList = toasts;
    this.toastListeners.forEach(listener => listener(toasts));
  }
}

/** Applies a patch to a state without changing it: the objects and the arrays on the path are copied. */
export function apply(state: Record<string, unknown>, patch: Patch): Record<string, unknown> {
  return update(state, patch.path, 0, patch) as Record<string, unknown>;
}

function update(node: unknown, path: readonly (string | number)[], depth: number, patch: Patch): unknown {
  if (depth === path.length) {
    if (patch.op === 'set') return patch.value;
    const array = Array.isArray(node) ? [...node] : [];
    array.splice(patch.index ?? 0, patch.remove ?? 0, ...((patch.value as unknown[] | undefined) ?? []));
    return array;
  }
  const key = path[depth]!;
  if (Array.isArray(node)) {
    const copy = [...node];
    copy[Number(key)] = update(node[Number(key)], path, depth + 1, patch);
    return copy;
  }
  const object = (typeof node === 'object' && node !== null ? node : {}) as Record<string, unknown>;
  return { ...object, [key]: update(object[key as string], path, depth + 1, patch) };
}
