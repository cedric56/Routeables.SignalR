import { html, nothing } from 'lit';
import type { Page } from '@routeables/client';
import { canRun, dialogGroups, routeables } from '../routeables';
import type { State } from '../routeables';

/**
 * The buttons that open the dialogs and the notifications. A dialog goes to the browser (see interactions.ts) and the view model waits
 * for the answer on the server, like it waits for a window on a desktop.
 */
export const dialogs = (page: Page, target: string) => {
  const s = page.state as State;
  return html`
    <div class="about-page">
      ${dialogGroups.map(group => html`
        <section class="about-group">
          <h2>${group.title}</h2>
          <div class="actions">
            ${group.actions.map(action => html`
              <button type="button" ?disabled=${!canRun(s[action.command])} @click=${() => routeables.run(target, s[action.command])}>${action.label}</button>`)}
          </div>
        </section>`)}
      ${s.hasResult ? html`<p class="about-result" role="status">${s.result}</p>` : nothing}
    </div>`;
};
