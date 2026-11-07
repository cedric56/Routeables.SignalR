import { html } from 'lit';
import { classMap } from 'lit/directives/class-map.js';
import { repeat } from 'lit/directives/repeat.js';
import type { Page } from '@routeables/client';
import { routeables } from '../routeables';
import type { State } from '../routeables';

/** The header and the footer of the Regions page. */
export const layoutBar = (page: Page) => {
  const s = page.state as State;
  return html`
    <div class=${classMap({ 'rg-bar': true, strong: !!s.isStrong })}>
      <span class="rg-bar-glyph">${s.glyph}</span>
      <div class="rg-bar-text">
        <strong>${s.title}</strong>
        <span>${s.detail}</span>
      </div>
    </div>`;
};

/** The logo the content region of the Regions page shows. */
export const layoutLogo = (page: Page) => html`<div class="rg-logo"><img src="/images/logo.svg" alt="${(page.state as State).name} logo" /></div>`;

interface Item { name: string; glyph: string; isCurrent: boolean; select: unknown }

/** The menu of the left panel. It is in a region of its own: its commands are called on that region. */
export const layoutMenu = (page: Page, target: string) => {
  const s = page.state as State;
  const items = (s.items as Item[] | undefined) ?? [];
  return html`
    <nav class="rg-menu">
      <div class="rg-menu-moves">
        <button type="button" ?disabled=${!s.canGoBack} @click=${() => routeables.run(target, s.backCommand)}>◀ Back</button>
        <button type="button" ?disabled=${!s.canGoForward} @click=${() => routeables.run(target, s.forwardCommand)}>Forward ▶</button>
      </div>
      ${repeat(items, item => item.name, item => html`
        <button type="button" class=${classMap({ 'rg-menu-item': true, current: item.isCurrent })} aria-current=${item.isCurrent ? 'page' : 'false'}
                @click=${() => routeables.run(target, item.select)}>
          <span class="rg-menu-glyph">${item.glyph}</span>
          <span>${item.name}</span>
        </button>`)}
    </nav>`;
};
