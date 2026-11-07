import { firstSegment, routeables } from './routeables';
import type { MenuEntry, Snapshot, State } from './routeables';
import { classes, fill, h, link } from './dom';
import { interactions } from './interactions';
import { PageSlot } from './page-view';

/**
 * The application, without a framework: the elements are created once, and what changes is drawn again when the snapshot changes. The
 * page of the main router is updated while it is the same page, and replaced when another one arrives.
 */
const go = (route: string): void => void routeables.goTo(route);

const drawerToggle = h('button', { type: 'button', class: 'drawer-toggle', 'aria-controls': 'drawer', 'aria-label': 'Show or hide the menu',
  onclick: () => routeables.toggleDrawer() }, '☰');
// The theme is asked of the view model like on the other platforms; Default follows the browser.
const themeButton = h('button', { type: 'button', class: 'drawer-toggle theme-form',
  onclick: () => void routeables.run('$shell', routeables.snapshot.shell?.state['themeCommand']) }, '◐');
const drawer = h('nav', { class: 'drawer', id: 'drawer', 'aria-label': 'Menu' });
const message = h('div', { style: 'display: contents' });
const content = h('div', { style: 'display: contents' });
const shell = h('div', { class: 'shell' },
  drawer,
  h('div', { class: 'shell-content' }, h('div', { style: 'padding: 20px' }, message, content)));
const overlays = interactions();

document.getElementById('app')!.replaceWith(
  h('header', { class: 'site-header' }, drawerToggle, link('brand', '/cats', go, 'Routeables Animals'), themeButton),
  h('main', null, shell),
  overlays.node);

const page = new PageSlot(content, '');
let drawnShell: unknown;
let drawnRoute: string | undefined;

function draw(snapshot: Snapshot): void {
  const theme = String((snapshot.shell?.state as State | undefined)?.['theme'] ?? 'Default');
  // The theme the view models asked for (IRouteableRequestedThemeService). Default follows the browser.
  document.documentElement.setAttribute('data-theme', theme.toLowerCase());
  themeButton.title = `Theme: ${theme} (click for the next one)`;
  themeButton.setAttribute('aria-label', `Change the theme, now ${theme}`);
  drawerToggle.setAttribute('aria-expanded', String(!snapshot.drawerClosed));
  shell.classList.toggle('drawer-closed', snapshot.drawerClosed);

  // The side panel: one entry per entry of the main view model, as in the other samples. It is drawn again when the menu or the page changes.
  if (snapshot.shell !== drawnShell || snapshot.page?.route !== drawnRoute) {
    drawnShell = snapshot.shell;
    drawnRoute = snapshot.page?.route;
    /** The menu marks the first segment of the address: /cats/Tom is still in Cats. */
    const current = ('/' + firstSegment(snapshot.page?.route ?? '')).toLowerCase();
    const entries = (snapshot.shell?.state['entries'] as MenuEntry[] | undefined) ?? [];
    fill(drawer, entries.map(entry => entry.isLocked
      // A page published a lock (Features): the entry cannot be followed until it publishes the unlock.
      ? h('span', { class: 'drawer-link locked', title: 'Locked' },
          h('span', { class: 'drawer-icon', 'aria-hidden': 'true' }, entry.glyph), h('span', { class: 'drawer-name' }, entry.name))
      : (() => {
          const active = entry.route.toLowerCase() === current;
          const a = link(classes('drawer-link', { active }), entry.route, go,
            h('span', { class: 'drawer-icon', 'aria-hidden': 'true' }, entry.glyph),
            h('span', { class: 'drawer-text' }, h('span', { class: 'drawer-name' }, entry.name),
              entry.description && h('span', { class: 'drawer-description' }, entry.description)));
          if (active) a.setAttribute('aria-current', 'page');
          return a;
        })()));
  }

  fill(message,
    snapshot.error && h('p', { class: 'error', role: 'alert' }, snapshot.error),
    !snapshot.page && h('p', { class: 'empty' }, snapshot.connected ? 'Loading…' : 'Offline: the server is not reachable.'));
  page.show(snapshot.page);
  overlays.update(snapshot);
}

routeables.subscribe(draw);
draw(routeables.snapshot);
void routeables.start();
