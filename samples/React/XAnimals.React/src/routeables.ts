import { MODAL, PAGE, RouteablesClient, signalRTransport } from '@routeables/client';
import type { CommandRef, Dialog, Page, Toast } from '@routeables/client';

/**
 * The state of a page, as the server sends it. Its shape is the one of the view model, which the component that draws it knows:
 * a component reads `s.title` as it would read a property of the view model.
 */
// eslint-disable-next-line @typescript-eslint/no-explicit-any
export type State = any;

/** What the application shows, at one moment: a new object each time something changes. */
export interface Snapshot {
  /** The page the server made current; null before the first one (it stays while the connection is down: the session is resumed). */
  readonly page: Page | null;
  /** The view model of the modal that is open (the note), or null. */
  readonly modal: Page | null;
  /** The menu and the theme (target `$shell`). */
  readonly shell: Page | null;
  readonly dialogs: readonly Dialog[];
  readonly toasts: readonly Toast[];
  /** Whether the side panel is hidden. Open on a wide screen unless the user closed it; the choice is kept. */
  readonly drawerClosed: boolean;
  readonly connected: boolean;
  readonly error: string | null;
}

// /routeables is the hub of Routeables.SignalR: `npm start` proxies it to the server (vite.config.ts).
const client = new RouteablesClient(signalRTransport('/routeables'));
const timers = new Set<number>();
const listeners = new Set<(snapshot: Snapshot) => void>();

function savedDrawerClosed(): boolean {
  try {
    const saved = localStorage.getItem('xanimals.drawer');
    return saved === 'closed' || (saved === null && innerWidth < 760);
  } catch {
    return innerWidth < 760;
  }
}

let starting: Promise<void> | null = null;
let snapshot: Snapshot = {
  page: null, modal: null, shell: null, dialogs: [], toasts: [], drawerClosed: savedDrawerClosed(), connected: false, error: null,
};

function update(change: Partial<Snapshot>): void {
  snapshot = { ...snapshot, ...change };
  listeners.forEach(listener => listener(snapshot));
}

/**
 * The view models of the server, as one snapshot the framework watches (`subscribe`, `snapshot`). The server decides what is shown:
 * this module shows what it sends and passes on what the user does. The address bar follows the page, and a page can be opened from
 * an address (a link, a reload, the back button). The same file is in every web sample; only the way the framework watches it changes.
 *
 * What the server shows is a set of targets (see @routeables/client): the page of the main router, the modal, the regions, and
 * the view models the server registered (`$shell` is the menu, `$pets` the cards of the Adopt page).
 */
export const routeables = {
  /** What is shown now. */
  get snapshot(): Snapshot {
    return snapshot;
  },

  /** Calls <paramref name="listener"/> at every change. Returns the way to stop. */
  subscribe(listener: (snapshot: Snapshot) => void): () => void {
    listeners.add(listener);
    return () => listeners.delete(listener);
  },

  toggleDrawer(): void {
    update({ drawerClosed: !snapshot.drawerClosed });
    try {
      localStorage.setItem('xanimals.drawer', snapshot.drawerClosed ? 'closed' : 'open');
    } catch {
      // The choice is not kept.
    }
  },

  /** Connects, then opens the page of the address. Once: a framework that mounts a component twice (React's StrictMode) connects once. */
  start(): Promise<void> {
    return (starting ??= (async () => {
      try {
        await client.connect();
        update({ connected: true });
        await routeables.goTo(location.pathname === '/' ? '/cats' : location.pathname + location.search);
      } catch {
        update({ error: 'The server is not reachable.' });
      }
    })());
  },

  /** Follows what a target shows (a region, `$name`): the server sends it while somebody follows it. Returns the way to stop. */
  follow(target: string, listener: (page: Page | null) => void): () => void {
    return client.subscribeTo(target, listener);
  },

  async goTo(route: string): Promise<void> {
    update({ error: null });
    const ack = await client.goTo(route);
    if (!ack.success) update({ error: ack.error ?? 'This page does not exist.' });
  },

  /** Goes to a route in the router of a region. */
  async goToIn(target: string, route: string): Promise<void> {
    const ack = await client.goToIn(target, route);
    if (!ack.success) update({ error: ack.error ?? 'This page does not exist.' });
  },

  /** Calls a command of the state (the `...Command` properties of a view model) of what a target shows. */
  async run(target: string, command: unknown): Promise<void> {
    if (command === null || command === undefined) return;
    const ack = await client.run(target, command as CommandRef);
    if (!ack.success) update({ error: ack.error ?? 'The command failed.' });
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

routeables.follow('$shell', shell => update({ shell }));
client.subscribeTo(PAGE, page => {
  update({ page });
  // The address follows the page; the back button is handled below.
  if (page) {
    const address = '/' + page.route.replace(/^\/+/, '');
    if (location.pathname + location.search !== address) history.pushState(null, '', address);
  }
});
client.subscribeTo(MODAL, modal => update({ modal }));
client.subscribeDialogs(dialogs => update({ dialogs }));
client.subscribeToasts(toasts => {
  update({ toasts });
  // A notification closes by itself after its time.
  for (const toast of toasts) {
    if (toast.expirationSeconds === null || timers.has(toast.id)) continue;
    timers.add(toast.id);
    setTimeout(() => void routeables.dismiss(toast, false), toast.expirationSeconds * 1000);
  }
});
client.onFailure(failure => update({ error: failure.message }));
client.onError(error => update({ error: error.message }));

// After a reconnection the client resumes its session on the server: the pages, their state and the dialogs come back. When the
// session is gone (the server restarted), the client opens the last page again by itself.
client.onConnection(connected => update({ connected }));
addEventListener('popstate', () => void routeables.goTo(location.pathname + location.search));

/** The menu entries of `$shell`. */
export interface MenuEntry {
  name: string;
  route: string;
  glyph: string;
  description: string;
  isLocked: boolean;
}

/** The view model types the animal lists and details are drawn for. */
export const animalLists = ['Cats', 'Dogs', 'Monkeys', 'Elephants', 'Bears', 'Pandas'].map(name => `${name}ViewModel`);
export const animalDetails = [...['Cat', 'Dog', 'Monkey', 'Elephant', 'Bear'].map(name => `${name}DetailViewModel`), 'PandaDetail'];

/** The buttons of the Dialogs page: the command of the state each one runs. */
export const dialogGroups: { title: string; actions: { command: string; label: string }[] }[] = [
  {
    title: 'Messages',
    actions: [
      { command: 'okCommand', label: 'Ok' },
      { command: 'errorCommand', label: 'Error' },
      { command: 'yesNoCommand', label: 'Yes / No' },
      { command: 'okCancelCommand', label: 'Ok / Cancel' },
      { command: 'okAbortCommand', label: 'Ok / Abort' },
      { command: 'yesNoCancelCommand', label: 'Yes / No / Cancel' },
      { command: 'yesNoAbortCommand', label: 'Yes / No / Abort' },
    ],
  },
  {
    title: 'Rich dialogs',
    actions: [
      { command: 'contentDialogCommand', label: 'Content dialog' },
      { command: 'taskDialogCommand', label: 'Task dialog' },
      { command: 'progressDialogCommand', label: 'Task dialog with progress' },
      { command: 'modalCommand', label: 'Show modal' },
    ],
  },
  {
    title: 'Notifications',
    actions: [
      { command: 'informationCommand', label: 'Information' },
      { command: 'successCommand', label: 'Success' },
      { command: 'warningCommand', label: 'Warning' },
      { command: 'notificationErrorCommand', label: 'Error' },
      { command: 'systemNotificationCommand', label: 'System' },
    ],
  },
];

/** The tabs of the Tabs page: each is a route shown in the region "tabs-demo". */
export const tabs = [
  { header: 'Cats', route: 'cats' },
  { header: 'Dogs', route: 'dogs' },
  { header: 'Monkeys', route: 'monkeys' },
];

/** Whether a command of the state can run now (a missing command can: the server says no). */
export const canRun = (command: unknown): boolean => (command as { canExecute?: boolean } | undefined)?.canExecute !== false;

/** The first segment of a route: /cats/Abyssinian is in Cats. */
export const firstSegment = (route: string): string => route.split('/').filter(Boolean)[0] ?? '';
