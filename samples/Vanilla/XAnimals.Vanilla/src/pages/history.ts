import type { Page } from '@routeables/client';
import { routeables } from '../routeables';
import type { State } from '../routeables';
import { classes, fill, h } from '../dom';
import type { View } from '../dom';
import { region } from '../region';

interface Entry { number: number; name: string; glyph: string; caption: string; badge: string; isCurrent: boolean; isAhead: boolean }

/** The history of a region, as cards, and the frame that shows the page the region displays. */
export function history(page: Page, target: string): View {
  let s = page.state as State;
  const run = (command: string): void => void routeables.run(target, s[command]);

  // The zone of the "history" region is created once: nothing assigns the page, it shows what the router of the region displays.
  const zone = region('history', () => s.transition ?? 'None');
  const track = h('div', { class: 'nav-track', role: 'list' });
  const moves = h('div', { class: 'actions' });
  const transition = h('select', { 'aria-label': 'Transition', onchange: (e: Event) => void routeables.set(target, 'transition', (e.target as HTMLSelectElement).value) });
  const guard = h('button', { type: 'button', class: 'nav-check', onclick: () => void routeables.set(target, 'blockLeaving', !s.blockLeaving) });
  const status = h('p', { role: 'status' });
  const node = h('div', { class: 'about-page' },
    h('section', { class: 'about-group' }, track),
    h('section', { class: 'about-group' }, h('div', { class: 'nav-frame', inert: true }, zone.node)),
    h('section', { class: 'about-group' }, moves),
    h('section', { class: 'about-group' }, h('h2', null, 'Transition'), transition),
    h('section', { class: 'about-group' }, h('h2', null, 'CanLeaveAsync'), guard),
    status);

  const draw = (): void => {
    fill(track, ((s.entries as Entry[] | undefined) ?? []).map(entry =>
      h('div', { class: classes('nav-card', { current: entry.isCurrent, ahead: entry.isAhead, behind: !entry.isCurrent && !entry.isAhead }), role: 'listitem' },
        h('span', { class: 'nav-number' }, entry.number), h('span', { class: 'nav-badge' }, entry.badge), h('span', { class: 'nav-glyph' }, entry.glyph),
        h('span', { class: 'nav-name' }, entry.name), h('span', { class: 'nav-caption' }, entry.caption))));
    fill(moves,
      h('button', { type: 'button', onclick: () => run('pushCommand') }, '＋ Push'),
      h('button', { type: 'button', disabled: !s.canGoBack, onclick: () => run('backCommand') }, '◀ Back'),
      h('button', { type: 'button', disabled: !s.canGoForward, onclick: () => run('forwardCommand') }, 'Forward ▶'),
      h('button', { type: 'button', disabled: !s.canGoBack, onclick: () => run('popCommand') }, '↩ Pop'));
    fill(transition, ((s.transitions as string[] | undefined) ?? []).map(name => h('option', { value: name, selected: name === s.transition }, name)));
    guard.textContent = `${s.guardGlyph} ${s.guardText}`;
    status.className = classes('about-result nav-status', { refused: s.isRefused });
    status.textContent = (s.isRefused ? '🚫 ' : '') + (s.status ?? '');
  };
  draw();
  return { node, update: next => { s = next.state as State; draw(); }, destroy: zone.destroy };
}
