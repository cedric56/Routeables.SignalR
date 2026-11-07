import type { Page } from '@routeables/client';
import { firstSegment, routeables } from '../routeables';
import type { State } from '../routeables';
import { h, link, redraw } from '../dom';

interface Animal { name: string; location: string; details: string; photoUrl: string }

export const animalDetail = (page: Page) => redraw(page, current => {
  const animal = (current.state as State).animal as Animal | null | undefined;
  return h('div', { class: 'monkey-detail' },
    animal
      ? [h('h1', { class: 'title' }, animal.name), h('p', { class: 'location' }, animal.location),
         h('img', { src: animal.photoUrl, alt: animal.name, width: 260, height: 330 }), h('p', { class: 'body-text' }, animal.details)]
      : [h('h1', { class: 'title' }, 'Animal not found'), h('p', { class: 'body-text' }, 'This animal was not found.')],
    link('back-btn', '/' + firstSegment(current.route), route => void routeables.goTo(route), 'Back to list'));
});
