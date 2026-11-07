import { html } from 'lit';
import type { Page } from '@routeables/client';
import { routeables } from '../routeables';
import type { State } from '../routeables';

export const about = (page: Page, target: string) => {
  const s = page.state as State;
  return html`
    <div class="about-page">
      <div class="about-logo"><img src="/images/logo.svg" alt="Routeables logo" /></div>
      <h1><span class="app-name">Routeables Animals</span><span class="app-version">1.0</span></h1>
      <section class="about-group">
        <h2>Links</h2>
        <div class="actions">
          <a class="action-button" href="https://github.com/cedric56/Routeables" target="_blank" rel="noopener noreferrer">Learn more</a>
          <button type="button" @click=${() => routeables.run(target, s.polarBearsCommand)}>Polar bears</button>
        </div>
      </section>
    </div>`;
};
