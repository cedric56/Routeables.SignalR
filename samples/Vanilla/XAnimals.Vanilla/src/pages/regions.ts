import type { Page } from '@routeables/client';
import { routeables } from '../routeables';
import type { State } from '../routeables';
import { h } from '../dom';
import type { View } from '../dom';
import { region } from '../region';

/** An application of its own: four zones, four routers. Each zone shows whatever the router of its region displays. */
export function regions(page: Page, target: string): View {
  let s = page.state as State;
  // The four zones are created once; what changes is the panel and the credits.
  const zones = ['shell-header', 'shell-left', 'shell-content', 'shell-footer'].map(name => region(name));
  const collapse = h('button', { type: 'button', class: 'rg-collapse', onclick: () => void routeables.run(target, s.toggleLeftCommand) });
  // One navigation made of segments: the logo in the content, the author in the footer, the copyright in the left panel.
  const credits = h('button', { type: 'button', class: 'rg-credits', onclick: () => void routeables.run(target, s.creditsCommand) });
  const node = h('div', { class: 'rg-app' },
    h('header', { class: 'rg-header' }, zones[0]!.node),
    h('aside', { class: 'rg-left' }, collapse, h('div', { class: 'rg-left-region' }, zones[1]!.node), credits),
    h('main', { class: 'rg-content' }, zones[2]!.node),
    h('footer', { class: 'rg-footer' }, zones[3]!.node));

  const draw = (): void => {
    const label = s.isLeftCollapsed ? 'Show the panel' : 'Reduce the panel';
    node.classList.toggle('left-collapsed', !!s.isLeftCollapsed);
    collapse.title = label;
    collapse.setAttribute('aria-label', label);
    collapse.setAttribute('aria-expanded', String(!s.isLeftCollapsed));
    collapse.textContent = s.isLeftCollapsed ? '»' : '«';
    credits.textContent = s.creditsText ?? '';
  };
  draw();
  return { node, update: next => { s = next.state as State; draw(); }, destroy: () => zones.forEach(zone => zone.destroy()) };
}
