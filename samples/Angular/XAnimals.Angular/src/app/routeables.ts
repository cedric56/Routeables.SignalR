import { Injectable, signal } from '@angular/core';
import type { Signal } from '@angular/core';
import { MODAL, PAGE, RouteablesClient, signalRTransport } from '@routeables/client';
import type { CommandRef, Dialog, Page, Toast } from '@routeables/client';

/**
 * The state of a page, as the server sends it. Its shape is the one of the view model, which the component that draws it knows:
 * a template reads `s().title` as it would read a property of the view model.
 */
// eslint-disable-next-line @typescript-eslint/no-explicit-any
export type State = any;

/**
 * The view models of the server, as signals. The server decides what is shown: this service shows what it sends and passes on what the
 * user does. The address bar follows the page, and a page can be opened from an address (a link, a reload, the back button).
 *
 * What the server shows is a set of targets (see @routeables/client): the page of the main router, the modal, the regions, and
 * the view models the server registered (`$shell` is the menu, `$pets` the cards of the Adopt page).
 */
@Injectable({ providedIn: 'root' })
export class Routeables {
  // /routeables is the hub of Routeables.SignalR: `ng serve` proxies it to the server (proxy.conf.json).
  private readonly client = new RouteablesClient(signalRTransport('/routeables'));

  /** The page the server made current; null before the first one (it stays while the connection is down: the session is resumed). */
  readonly page = signal<Page | null>(null);
  /** The view model of the modal that is open (the note), or null. */
  readonly modal = signal<Page | null>(null);
  /** The menu and the theme (target `$shell`). */
  readonly shell = this.follow('$shell');
  readonly dialogs = signal<readonly Dialog[]>([]);
  readonly toasts = signal<readonly Toast[]>([]);
  /** Whether the side panel is hidden. Open on a wide screen unless the user closed it; the choice is kept. */
  readonly drawerClosed = signal(Routeables.savedDrawerClosed());
  readonly connected = signal(false);
  readonly error = signal<string | null>(null);

  constructor() {
    this.client.subscribeTo(PAGE, page => {
      this.page.set(page);
      // The address follows the page; the back button is handled below.
      if (page) {
        const address = '/' + page.route.replace(/^\/+/, '');
        if (location.pathname + location.search !== address) history.pushState(null, '', address);
      }
    });
    this.client.subscribeTo(MODAL, modal => this.modal.set(modal));
    this.client.subscribeDialogs(dialogs => this.dialogs.set(dialogs));
    this.client.subscribeToasts(toasts => {
      this.toasts.set(toasts);   // the client closes a notification after its time
    });
    this.client.onFailure(failure => this.error.set(failure.message));
    this.client.onError(error => this.error.set(error.message));

    // After a reconnection the client resumes its session on the server: the pages, their state and the dialogs come back. When the
    // session is gone (the server restarted), the client opens the last page again by itself.
    this.client.onConnection(connected => this.connected.set(connected));
    addEventListener('popstate', () => void this.goTo(location.pathname + location.search));
  }

  private static savedDrawerClosed(): boolean {
    try {
      const saved = localStorage.getItem('xanimals.drawer');
      return saved === 'closed' || (saved === null && innerWidth < 760);
    } catch {
      return innerWidth < 760;
    }
  }

  toggleDrawer(): void {
    this.drawerClosed.update(closed => !closed);
    try {
      localStorage.setItem('xanimals.drawer', this.drawerClosed() ? 'closed' : 'open');
    } catch {
      // The choice is not kept.
    }
  }

  async start(): Promise<void> {
    try {
      await this.client.connect();
      this.connected.set(true);
      await this.goTo(location.pathname === '/' ? '/cats' : location.pathname + location.search);
    } catch {
      this.error.set('The server is not reachable.');
    }
  }

  /** Follows a target (a region, `$name`) as a signal, for as long as the caller lives: call it where a component is created. */
  follow(target: string): Signal<Page | null> {
    const page = signal<Page | null>(null);
    this.client.subscribeTo(target, p => page.set(p));
    return page.asReadonly();
  }

  /** Follows a target and stops when told to: for a component whose target changes. */
  watch(target: string, listener: (page: Page | null) => void): () => void {
    return this.client.subscribeTo(target, listener);
  }

  async goTo(route: string): Promise<void> {
    this.error.set(null);
    const ack = await this.client.goTo(route);
    if (!ack.success) this.error.set(ack.error ?? 'This page does not exist.');
  }

  /** Goes to a route in the router of a region. */
  async goToIn(target: string, route: string): Promise<void> {
    const ack = await this.client.goToIn(target, route);
    if (!ack.success) this.error.set(ack.error ?? 'This page does not exist.');
  }

  /** Calls a command of the state (the `...Command` properties of a view model) of what a target shows. */
  async run(target: string, command: unknown): Promise<void> {
    const ack = await this.client.run(target, command as CommandRef);
    if (!ack.success) this.error.set(ack.error ?? 'The command failed.');
  }

  /** Sets a property of what a target shows (a text box, a check box); the server confirms with the change. */
  async set(target: string, property: string, value: unknown): Promise<void> {
    await this.client.setOn(target, property, value);
  }

  /** Answers a dialog: the index of the button the user chose. */
  async answer(dialog: Dialog, button: number): Promise<void> {
    await this.client.answer(dialog, button);
  }

  /** Closes a notification, or tells that the user clicked it. */
  async dismiss(toast: Toast, click: boolean): Promise<void> {
    await this.client.dismiss(toast, click);
  }
}
