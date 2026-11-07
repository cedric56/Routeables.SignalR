import type { Page } from '@routeables/client';
import { firstSegment, routeables } from '../routeables';
import type { State } from '../routeables';
import { fill, h, link } from '../dom';
import type { View } from '../dom';

interface Animal { name: string; location: string; details: string; thumbnailUrl: string; photoUrl: string }

/** The list of a species. A list that a region shows (hostsDetail is false) does not leave the page: no search, no link to a detail. */
export function animalList(page: Page, target: string): View {
  // The first segment of the address of a page: /cats/Abyssinian is the page of the cats.
  const kind = firstSegment(page.route);
  const s = page.state as State;

  // The search box is created once: it keeps what is typed, the server answers each key after the next one may already be typed.
  const search = s.hostsDetail
    ? h('div', { class: 'search-row', style: 'display: flex; gap: 0.5rem; align-items: center' },
        h('input', { class: 'search-box', style: 'flex: 1', type: 'search', placeholder: 'Enter search term', 'aria-label': 'Search animals',
          value: s.searchText ?? '', oninput: (e: Event) => void routeables.set(target, 'searchText', (e.target as HTMLInputElement).value) }))
    : null;
  const empty = h('div', { style: 'display: contents' });
  const list = h('div', { class: 'animal-list' });
  const node = h('div', { class: 'animals-page' }, h('h1', null, kind.charAt(0).toUpperCase() + kind.slice(1)), search, empty, list);

  const draw = (current: Page): void => {
    const state = current.state as State;
    const animals = (state.filteredAnimals as Animal[] | undefined) ?? [];
    fill(empty, !animals.length && h('p', { class: 'no-results' }, `No ${kind} found.`));
    fill(list, animals.map(animal => {
      const item = [
        h('img', { src: animal.thumbnailUrl, alt: animal.name, width: 56, height: 68, loading: 'lazy' }),
        h('span', { class: 'animal-text' }, h('span', { class: 'animal-name' }, animal.name), h('span', { class: 'animal-location' }, animal.location)),
      ];
      // A link is a route: the server navigates and the page that arrives replaces this one. No reload.
      return state.hostsDetail
        ? link('animal-item', `/${kind}/${encodeURIComponent(animal.name)}`, route => void routeables.goTo(route), item)
        : h('div', { class: 'animal-item' }, item);
    }));
  };
  draw(page);
  return { node, update: draw, destroy: () => {} };
}
