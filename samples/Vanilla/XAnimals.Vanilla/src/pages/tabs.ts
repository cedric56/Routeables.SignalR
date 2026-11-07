import type { Page } from '@routeables/client';
import { routeables, tabs as items } from '../routeables';
import { h } from '../dom';
import type { View } from '../dom';
import { region } from '../region';

/** The items of a tab control are routes: each tab shows its route in the region of the page, and the region draws it. */
export function tabs(_page: Page, _target: string): View {
  const zone = region('tabs-demo');
  const buttons = items.map(tab => h('button', { type: 'button', role: 'tab', class: 'adopt-choice', onclick: () => select(tab.route) }, tab.header));
  const node = h('div', { style: 'display: contents' },
    h('div', { class: 'about-page' }, h('div', { class: 'actions', role: 'tablist' }, buttons)),
    h('div', { class: 'about-page' }, h('section', { class: 'about-group' }, zone.node)));

  function select(route: string): void {
    items.forEach((tab, index) => buttons[index]!.setAttribute('aria-selected', String(tab.route === route)));
    void routeables.goToIn('tabs-demo', route);
  }

  // A tab control selects its first item when it appears.
  select(items[0]!.route);
  return { node, update: () => {}, destroy: zone.destroy };
}
