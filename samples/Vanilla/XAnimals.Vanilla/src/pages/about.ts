import type { Page } from '@routeables/client';
import { routeables } from '../routeables';
import type { State } from '../routeables';
import { h, redraw } from '../dom';

export const about = (page: Page, target: string) => redraw(page, ({ state }) => {
  const s = state as State;
  return h('div', { class: 'about-page' },
    h('div', { class: 'about-logo' }, h('img', { src: '/images/logo.svg', alt: 'Routeables logo' })),
    h('h1', null, h('span', { class: 'app-name' }, 'Routeables Animals'), h('span', { class: 'app-version' }, '1.0')),
    h('section', { class: 'about-group' },
      h('h2', null, 'Links'),
      h('div', { class: 'actions' },
        h('a', { class: 'action-button', href: 'https://github.com/cedric56/Routeables', target: '_blank', rel: 'noopener noreferrer' }, 'Learn more'),
        h('button', { type: 'button', onclick: () => void routeables.run(target, s.polarBearsCommand) }, 'Polar bears'))));
});
