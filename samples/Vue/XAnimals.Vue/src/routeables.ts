import { getCurrentScope, onScopeDispose, ref, shallowRef } from 'vue';
import type { ShallowRef } from 'vue';
import { MODAL, PAGE, RouteablesClient, signalRTransport } from '@routeables/client';
import type { CommandRef, Dialog, Page, Toast } from '@routeables/client';

/**
 * The state of a page, as the server sends it. Its shape is the one of the view model, which the component that draws it knows:
 * a template reads `s.title` as it would read a property of the view model.
 */
// eslint-disable-next-line @typescript-eslint/no-explicit-any
export type State = any;

// /routeables is the hub of Routeables.SignalR: `npm start` proxies it to the server (vite.config.ts).
const client = new RouteablesClient(signalRTransport('/routeables'));
const timers = new Set<number>();

function savedDrawerClosed(): boolean {
  try {
    const saved = localStorage.getItem('xanimals.drawer');
    return saved === 'closed' || (saved === null && innerWidth < 760);
  } catch {
    return innerWidth < 760;
  }
}

/**
 * The view models of the server, as refs. The server decides what is shown: this module shows what it sends and passes on what the
 * user does. The address bar follows the page, and a page can be opened from an address (a link, a reload, the back button).
 *
 * What the server shows is a set of targets (see @routeables/client): the page of the main router, the modal, the regions, and
 * the view models the server registered (`$shell` is the menu, `$pets` the cards of the Adopt page).
 */
export const routeables = {
  /** The page the server made current; null before the first one, and while the connection is down. */
  page: shallowRef<Page | null>(null),
  /** The view model of the modal that is open (the note), or null. */
  modal: shallowRef<Page | null>(null),
  dialogs: shallowRef<readonly Dialog[]>([]),
  toasts: shallowRef<readonly Toast[]>([]),
  /** Whether the side panel is hidden. Open on a wide screen unless the user closed it; the choice is kept. */
  drawerClosed: ref(savedDrawerClosed()),
  connected: ref(false),
  error: ref<string | null>(null),
  /** The menu and the theme (target `$shell`). */
  shell: null as unknown as ShallowRef<Page | null>,

  toggleDrawer(): void {
    routeables.drawerClosed.value = !routeables.drawerClosed.value;
    try {
      localStorage.setItem('xanimals.drawer', routeables.drawerClosed.value ? 'closed' : 'open');
    } catch {
      // The choice is not kept.
    }
  },

  async start(): Promise<void> {
    try {
      await client.connect();
      routeables.connected.value = true;
      await routeables.goTo(location.pathname === '/' ? '/cats' : location.pathname + location.search);
    } catch {
      routeables.error.value = 'The server is not reachable.';
    }
  },

  /** Follows a target (a region, `$name`) as a ref, until the component that called it goes away: call it in a `setup`. */
  follow(target: string): ShallowRef<Page | null> {
    const page = shallowRef<Page | null>(null);
    const stop = client.subscribeTo(target, p => (page.value = p));
    if (getCurrentScope()) onScopeDispose(stop);
    return page;
  },

  /** Follows a target and stops when told to: for a component whose target changes. */
  watch(target: string, listener: (page: Page | null) => void): () => void {
    return client.subscribeTo(target, listener);
  },

  async goTo(route: string): Promise<void> {
    routeables.error.value = null;
    const ack = await client.goTo(route);
    if (!ack.success) routeables.error.value = ack.error ?? 'This page does not exist.';
  },

  /** Goes to a route in the router of a region. */
  async goToIn(target: string, route: string): Promise<void> {
    const ack = await client.goToIn(target, route);
    if (!ack.success) routeables.error.value = ack.error ?? 'This page does not exist.';
  },

  /** Calls a command of the state (the `...Command` properties of a view model) of what a target shows. */
  async run(target: string, command: unknown): Promise<void> {
    const ack = await client.run(target, command as CommandRef);
    if (!ack.success) routeables.error.value = ack.error ?? 'The command failed.';
  },

  /** Sets a property of what a target shows (a text box, a check box); the server confirms with the change. */
  async set(target: string, property: string, value: unknown): Promise<void> {
    await client.setOn(target, property, value);
  },

  /** Answers a dialog: the index of the button the user chose. */
  async answer(dialog: Dialog, button: number): Promise<void> {
    await client.answer(dialog, button);
  },

  /** Closes a notification, or tells that the user clicked it. */
  async dismiss(toast: Toast, click: boolean): Promise<void> {
    timers.delete(toast.id);
    await client.dismiss(toast, click);
  },
};

routeables.shell = routeables.follow('$shell');

client.subscribeTo(PAGE, page => {
  routeables.page.value = page;
  // The address follows the page; the back button is handled below.
  if (page) {
    const address = '/' + page.route.replace(/^\/+/, '');
    if (location.pathname + location.search !== address) history.pushState(null, '', address);
  }
});
client.subscribeTo(MODAL, modal => (routeables.modal.value = modal));
client.subscribeDialogs(dialogs => (routeables.dialogs.value = dialogs));
client.subscribeToasts(toasts => {
  routeables.toasts.value = toasts;
  // A notification closes by itself after its time.
  for (const toast of toasts) {
    if (toast.expirationSeconds === null || timers.has(toast.id)) continue;
    timers.add(toast.id);
    setTimeout(() => void routeables.dismiss(toast, false), toast.expirationSeconds * 1000);
  }
});
client.onFailure(failure => (routeables.error.value = failure.message));

// The server keeps the view models of a connection: after a reconnection, the page is opened again.
client.onConnection(connected => {
  routeables.connected.value = connected;
  if (connected) void routeables.goTo(location.pathname + location.search);
});
addEventListener('popstate', () => void routeables.goTo(location.pathname + location.search));
