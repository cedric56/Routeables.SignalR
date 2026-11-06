import { MODAL, PAGE } from './types.js';
import type {
  Ack, ChangedMessage, ClosedMessage, CommandRef, Dialog, FailureMessage, NavigatedMessage, Page, RouteableTransport, Toast,
} from './types.js';

export type PageListener = (page: Page | null) => void;
export type FailureListener = (failure: FailureMessage) => void;
export type ConnectionListener = (connected: boolean) => void;
export type DialogListener = (dialogs: readonly Dialog[]) => void;
export type ToastListener = (toasts: readonly Toast[]) => void;

/**
 * Holds what the server shows, and sends what the user does. It does not know Angular or Vue: a component subscribes
 * and shows `page.state`; a framework layer turns the subscription into signals, observables or refs.
 *
 * What the server shows is a set of targets: the page of the main router (""), the modal ("$modal"), the regions (by their
 * name) and the view models the application registered ("$name"). The page and the modal are always sent; the others
 * are sent while somebody watches them (`watch`).
 */
export class RouteablesClient {
  private readonly pages = new Map<string, Page | null>();
  private readonly pageListeners = new Map<string, Set<PageListener>>();
  private readonly watchers = new Map<string, number>();
  private readonly failureListeners = new Set<FailureListener>();
  private readonly connectionListeners = new Set<ConnectionListener>();
  private readonly dialogListeners = new Set<DialogListener>();
  private readonly toastListeners = new Set<ToastListener>();
  private dialogList: readonly Dialog[] = [];
  private toastList: readonly Toast[] = [];
  private connected = false;
  private sequence = 0;

  constructor(private readonly transport: RouteableTransport) {
    transport.onConnectionChange(connected => {
      this.connected = connected;
      if (!connected) {
        // The server keeps the view models of a connection: a new connection starts with none, so what is shown is stale.
        for (const target of [...this.pages.keys()]) this.setPage(target, null);
        this.setDialogs([]);
        this.setToasts([]);
      } else {
        // The new connection watches nothing yet.
        for (const target of this.watchers.keys()) void this.transport.call('Watch', target);
      }
      this.connectionListeners.forEach(listener => listener(connected));
    });
  }

  /** The page of the main router, or null before the first one (and while disconnected). */
  get page(): Page | null {
    return this.pageOf(PAGE);
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

  /** Opens the connection. Go to a route afterwards. */
  async connect(): Promise<void> {
    await this.transport.start({
      navigated: message => this.onNavigated(message),
      changed: message => this.onChanged(message),
      failed: message => this.failureListeners.forEach(listener => listener(message)),
      dialogRequested: dialog => this.setDialogs([...this.dialogList, dialog]),
      notified: toast => this.setToasts([...this.toastList, toast]),
      closed: message => this.onClosed(message),
    });
    this.connected = true;
    for (const target of this.watchers.keys()) void this.transport.call('Watch', target);
  }

  disconnect(): Promise<void> {
    return this.transport.stop();
  }

  // ---------------------------------------------------------------- what the user does

  /** Goes to a route, as the address bar would. The page arrives through the subscription. */
  goTo(route: string): Promise<Ack> {
    return this.transport.call('GoTo', route);
  }

  /** Goes to a route in the router of a target: a region, by its name. */
  goToIn(target: string, route: string): Promise<Ack> {
    return target === PAGE ? this.goTo(route) : this.transport.call('GoToIn', target, route);
  }

  /** Calls a method of the current page, by its name. */
  invoke(command: string, ...args: unknown[]): Promise<Ack> {
    return this.transport.call('Invoke', command, args);
  }

  /** Calls a method of what a target shows, by its name. */
  invokeOn(target: string, command: string, ...args: unknown[]): Promise<Ack> {
    return this.transport.call('InvokeOn', target, command, args);
  }

  /** Calls a command of the state (a `CommandRef` found in the state of a target). */
  run(target: string, command: CommandRef, ...args: unknown[]): Promise<Ack> {
    return this.invokeOn(target, command.$command, ...args);
  }

  /** Sets a property of the current page (a text box, a check box). The server confirms by telling the change. */
  set(property: string, value: unknown): Promise<Ack> {
    return this.transport.call('Set', property, value);
  }

  /** Sets a property of what a target shows. */
  setOn(target: string, property: string, value: unknown): Promise<Ack> {
    return this.transport.call('SetOn', target, property, value);
  }

  /** Answers a dialog: the index of the button the user chose. */
  answer(dialog: Dialog, button: number): Promise<Ack> {
    this.setDialogs(this.dialogList.filter(d => d.id !== dialog.id));
    return this.transport.call('Answer', dialog.id, button);
  }

  /** Closes a notification, or tells that the user clicked it. */
  dismiss(toast: Toast, click = false): Promise<Ack> {
    this.setToasts(this.toastList.filter(t => t.id !== toast.id));
    return this.transport.call('Dismiss', toast.id, click);
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
    return () => {
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

  onFailure(listener: FailureListener): () => void {
    this.failureListeners.add(listener);
    return () => this.failureListeners.delete(listener);
  }

  onConnection(listener: ConnectionListener): () => void {
    this.connectionListeners.add(listener);
    return () => this.connectionListeners.delete(listener);
  }

  // ---------------------------------------------------------------- watchers

  private isAlwaysSent(target: string): boolean {
    return target === PAGE || target === MODAL;
  }

  private watch(target: string): void {
    if (this.isAlwaysSent(target)) return;
    const count = (this.watchers.get(target) ?? 0) + 1;
    this.watchers.set(target, count);
    if (count === 1 && this.connected) void this.transport.call('Watch', target);
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
    if (this.connected) void this.transport.call('Unwatch', target);
  }

  // ---------------------------------------------------------------- what the server tells

  private onNavigated(message: NavigatedMessage): void {
    const target = message.target ?? PAGE;
    this.setPage(target, {
      sequence: ++this.sequence,
      target,
      route: message.route,
      viewModel: message.viewModel,
      state: message.state,
      commands: message.commands,
    });
  }

  private onChanged(message: ChangedMessage): void {
    const target = message.target ?? PAGE;
    const page = this.pageOf(target);
    if (page === null) return;
    // A new object, so that a framework that compares references sees the change.
    this.setPage(target, { ...page, state: { ...page.state, [message.property]: message.value } });
  }

  private onClosed(message: ClosedMessage): void {
    this.setPage(message.target, null);
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
