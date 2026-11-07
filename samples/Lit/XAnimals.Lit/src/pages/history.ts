import { html } from 'lit';
import { classMap } from 'lit/directives/class-map.js';
import { repeat } from 'lit/directives/repeat.js';
import type { Page } from '@routeables/client';
import { routeables } from '../routeables';
import type { State } from '../routeables';
import '../region';

interface Entry { number: number; name: string; glyph: string; caption: string; badge: string; isCurrent: boolean; isAhead: boolean }

/** The history of a region, as cards, and the frame that shows the page the region displays. */
export const history = (page: Page, target: string) => {
  const s = page.state as State;
  const entries = (s.entries as Entry[] | undefined) ?? [];
  const transitions = (s.transitions as string[] | undefined) ?? [];
  const run = (command: string): void => void routeables.run(target, s[command]);
  return html`
    <div class="about-page">
      <section class="about-group">
        <div class="nav-track" role="list">
          ${repeat(entries, entry => entry.number, entry => html`
            <div class=${classMap({ 'nav-card': true, current: entry.isCurrent, ahead: entry.isAhead, behind: !entry.isCurrent && !entry.isAhead })} role="listitem">
              <span class="nav-number">${entry.number}</span>
              <span class="nav-badge">${entry.badge}</span>
              <span class="nav-glyph">${entry.glyph}</span>
              <span class="nav-name">${entry.name}</span>
              <span class="nav-caption">${entry.caption}</span>
            </div>`)}
        </div>
      </section>

      <section class="about-group">
        <!-- The zone of the "history" region: nothing assigns the page, it shows what the router of the region displays. -->
        <div class="nav-frame" inert><xa-region name="history" transition=${s.transition ?? 'None'}></xa-region></div>
      </section>

      <section class="about-group">
        <div class="actions">
          <button type="button" @click=${() => run('pushCommand')}>＋ Push</button>
          <button type="button" ?disabled=${!s.canGoBack} @click=${() => run('backCommand')}>◀ Back</button>
          <button type="button" ?disabled=${!s.canGoForward} @click=${() => run('forwardCommand')}>Forward ▶</button>
          <button type="button" ?disabled=${!s.canGoBack} @click=${() => run('popCommand')}>↩ Pop</button>
        </div>
      </section>

      <section class="about-group">
        <h2>Transition</h2>
        <select aria-label="Transition" @change=${(e: Event) => routeables.set(target, 'transition', (e.target as HTMLSelectElement).value)}>
          ${transitions.map(name => html`<option value=${name} ?selected=${name === s.transition}>${name}</option>`)}
        </select>
      </section>

      <section class="about-group">
        <h2>CanLeaveAsync</h2>
        <button type="button" class="nav-check" @click=${() => routeables.set(target, 'blockLeaving', !s.blockLeaving)}>${s.guardGlyph} ${s.guardText}</button>
      </section>

      <p class=${classMap({ 'about-result': true, 'nav-status': true, refused: !!s.isRefused })} role="status">${s.isRefused ? '🚫 ' : ''}${s.status}</p>
    </div>`;
};
