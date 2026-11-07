import type { Page } from '@routeables/client';
import { routeables } from '../routeables';
import type { State } from '../routeables';
import { h, redraw } from '../dom';

export const members = (page: Page, target: string) => redraw(page, ({ state }) => {
  const s = state as State;
  return h('div', { class: 'about-page' },
    h('div', { class: 'about-group' },
      h('p', null, s.welcome),
      h('div', { class: 'actions' }, h('button', { type: 'button', onclick: () => void routeables.run(target, s.signOutCommand) }, 'Sign out'))));
});
