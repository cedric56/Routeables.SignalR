import type { Page } from '@routeables/client';
import { routeables } from '../routeables';
import type { State } from '../routeables';
import { h, redraw } from '../dom';

export const features = (page: Page, target: string) => redraw(page, ({ state }) => {
  const s = state as State;
  const button = (label: string, command: string, enabled = true) =>
    h('button', { type: 'button', disabled: !enabled, onclick: () => void routeables.run(target, s[command]) }, label);
  return h('div', { class: 'about-page' },
    h('section', { class: 'about-group' },
      h('h2', null, 'Guard and return'),
      h('p', null, 'The Members page is for members. Signed out, opening it brings you here; sign in, then open it.'),
      h('div', { class: 'actions' },
        button('Sign in', 'signInCommand', !!s.isSignedOut), button('Sign out', 'signOutCommand', !!s.isSignedIn), button('Open Members', 'openMembersCommand'))),
    h('section', { class: 'about-group' },
      h('h2', null, 'Link'),
      h('p', null, 'An address from outside, followed through the router. In a browser the address bar does the same job.'),
      h('div', { class: 'actions' }, button(`Open ${s.link}`, 'openLinkCommand'))),
    h('section', { class: 'about-group' },
      h('h2', null, 'Lock'),
      h('p', null, 'The page publishes a lock message; the menu entries are disabled until it publishes the unlock one.'),
      h('div', { class: 'actions' },
        button('Lock', 'lockCommand', !!s.canLock), button('Unlock', 'unlockCommand', !!s.canUnlock), button('Lock for 5 seconds', 'lockForCommand', !!s.canLock)),
      h('p', { class: 'about-result', role: 'status' }, s.lockStatus)),
    h('section', { class: 'about-group' },
      h('h2', null, 'Measures'),
      h('div', { class: 'actions' }, button('Show the last route changes', 'refreshCommand')),
      h('pre', null, s.measures)),
    h('p', { class: 'about-result', role: 'status' }, s.status));
});
