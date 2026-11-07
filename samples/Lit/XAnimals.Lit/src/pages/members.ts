import { html } from 'lit';
import type { Page } from '@routeables/client';
import { routeables } from '../routeables';
import type { State } from '../routeables';

export const members = (page: Page, target: string) => {
  const s = page.state as State;
  return html`
    <div class="about-page">
      <div class="about-group">
        <p>${s.welcome}</p>
        <div class="actions">
          <button type="button" @click=${() => routeables.run(target, s.signOutCommand)}>Sign out</button>
        </div>
      </div>
    </div>`;
};
