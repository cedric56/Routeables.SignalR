import type { Page } from '@routeables/client';
import { routeables } from '../routeables';
import type { State } from '../routeables';
import { classes, h, redraw } from '../dom';

/** The header and the footer of the Regions page. */
export const layoutBar = (page: Page) => redraw(page, ({ state }) => {
  const s = state as State;
  return h('div', { class: classes('rg-bar', { strong: s.isStrong }) },
    h('span', { class: 'rg-bar-glyph' }, s.glyph),
    h('div', { class: 'rg-bar-text' }, h('strong', null, s.title), h('span', null, s.detail)));
});

/** The logo the content region of the Regions page shows. */
export const layoutLogo = (page: Page) => redraw(page, ({ state }) =>
  h('div', { class: 'rg-logo' }, h('img', { src: '/images/logo.svg', alt: `${(state as State).name} logo` })));

interface Item { name: string; glyph: string; isCurrent: boolean; select: unknown }

/** The menu of the left panel. It is in a region of its own: its commands are called on that region. */
export const layoutMenu = (page: Page, target: string) => redraw(page, ({ state }) => {
  const s = state as State;
  return h('nav', { class: 'rg-menu' },
    h('div', { class: 'rg-menu-moves' },
      h('button', { type: 'button', disabled: !s.canGoBack, onclick: () => void routeables.run(target, s.backCommand) }, '◀ Back'),
      h('button', { type: 'button', disabled: !s.canGoForward, onclick: () => void routeables.run(target, s.forwardCommand) }, 'Forward ▶')),
    ((s.items as Item[] | undefined) ?? []).map(item =>
      h('button', { type: 'button', class: classes('rg-menu-item', { current: item.isCurrent }), 'aria-current': item.isCurrent ? 'page' : null,
        onclick: () => void routeables.run(target, item.select) },
        h('span', { class: 'rg-menu-glyph' }, item.glyph), h('span', null, item.name))));
});
