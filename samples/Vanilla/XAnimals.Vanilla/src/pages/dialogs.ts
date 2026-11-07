import type { Page } from '@routeables/client';
import { canRun, dialogGroups, routeables } from '../routeables';
import type { State } from '../routeables';
import { h, redraw } from '../dom';

/**
 * The buttons that open the dialogs and the notifications. A dialog goes to the browser (see interactions.ts) and the view model waits
 * for the answer on the server, like it waits for a window on a desktop.
 */
export const dialogs = (page: Page, target: string) => redraw(page, ({ state }) => {
  const s = state as State;
  return h('div', { class: 'about-page' },
    dialogGroups.map(group => h('section', { class: 'about-group' },
      h('h2', null, group.title),
      h('div', { class: 'actions' }, group.actions.map(action =>
        h('button', { type: 'button', disabled: !canRun(s[action.command]), onclick: () => void routeables.run(target, s[action.command]) }, action.label))))),
    s.hasResult && h('p', { class: 'about-result', role: 'status' }, s.result));
});
