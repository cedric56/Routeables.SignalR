import { html } from 'lit';
import { classMap } from 'lit/directives/class-map.js';
import type { Page } from '@routeables/client';
import { routeables } from '../routeables';
import type { State } from '../routeables';
import '../region';

/** An application of its own: four zones, four routers. Each zone shows whatever the router of its region displays. */
export const regions = (page: Page, target: string) => {
  const s = page.state as State;
  const label = s.isLeftCollapsed ? 'Show the panel' : 'Reduce the panel';
  return html`
    <div class=${classMap({ 'rg-app': true, 'left-collapsed': !!s.isLeftCollapsed })}>
      <header class="rg-header"><xa-region name="shell-header"></xa-region></header>
      <aside class="rg-left">
        <button type="button" class="rg-collapse" title=${label} aria-label=${label} aria-expanded=${String(!s.isLeftCollapsed)}
                @click=${() => routeables.run(target, s.toggleLeftCommand)}>${s.isLeftCollapsed ? '»' : '«'}</button>
        <div class="rg-left-region"><xa-region name="shell-left"></xa-region></div>
        <!-- One navigation made of segments: the logo in the content, the author in the footer, the copyright in the left panel. -->
        <button type="button" class="rg-credits" @click=${() => routeables.run(target, s.creditsCommand)}>${s.creditsText}</button>
      </aside>
      <main class="rg-content"><xa-region name="shell-content"></xa-region></main>
      <footer class="rg-footer"><xa-region name="shell-footer"></xa-region></footer>
    </div>`;
};
