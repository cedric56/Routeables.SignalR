import { routeables } from './routeables';
import { h } from './dom';
import { PageSlot } from './page-view';

/**
 * The zone of a region: it shows what the router of the region displays. Nothing assigns a page to it; the page is chosen by its
 * view model. A page that arrives plays the transition of the zone again (a new element).
 */
export function region(name: string, transition: () => string = () => 'None'): { node: HTMLElement; destroy(): void } {
  const node = h('div', { style: 'display: contents', 'data-region': name });
  const slot = new PageSlot(node, name, inner => h('div', { class: `rt-enter rt-${transition().toLowerCase()}` }, inner));
  const stop = routeables.follow(name, page => slot.show(page));
  return { node, destroy: () => { stop(); slot.destroy(); } };
}
