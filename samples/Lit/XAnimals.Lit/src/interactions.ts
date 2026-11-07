import { html, nothing } from 'lit';
import { customElement } from 'lit/decorators.js';
import { repeat } from 'lit/directives/repeat.js';
import { routeables } from './routeables';
import type { State } from './routeables';
import { LightElement, SnapshotController } from './base';

/**
 * What the services of the server put on the screen: the notifications, the dialog the user has to answer, and the modal (a view model of
 * the server, drawn here). The view model that asked waits on the server for the answer.
 */
@customElement('xa-interactions')
export class Interactions extends LightElement {
  private readonly app = new SnapshotController(this);

  protected override render() {
    const { toasts, dialogs, modal: modalPage } = this.app.value;
    const dialog = dialogs[0];
    const modal = modalPage?.state as State | undefined;

    return html`
      ${toasts.length ? html`
        <div class="web-toasts" aria-live="polite">
          ${repeat(toasts, toast => toast.id, toast => html`
            <div class="web-toast web-toast-${toast.type.toLowerCase()}">
              <button type="button" class="web-toast-body" @click=${() => routeables.dismiss(toast, true)}>
                <strong>${toast.title}</strong>
                <span>${toast.message}</span>
              </button>
              <button type="button" class="web-toast-close" aria-label="Close" @click=${() => routeables.dismiss(toast, false)}>✕</button>
            </div>`)}
        </div>` : nothing}

      ${modal ? html`
        <div class="web-dialog-backdrop">
          <div class="web-dialog" role="dialog" aria-modal="true" aria-labelledby="web-dialog-title">
            <h2 id="web-dialog-title">${modal.title}</h2>
            <!-- The box keeps what is typed (defaultValue): the server answers each key after the next one may already be typed. -->
            <input class="search-box" placeholder="Write a note" aria-label="Note" .defaultValue=${modal.note ?? ''}
                   @input=${(e: Event) => routeables.set('$modal', 'note', (e.target as HTMLInputElement).value)} />
            <div class="actions">
              ${repeat(modal.commands as State[], button => button.name, button => html`
                <button type="button" ?disabled=${!button.command.canExecute} @click=${() => routeables.run('$modal', button.command)}>${button.name}</button>`)}
            </div>
          </div>
        </div>` : dialog ? html`
        <div class="web-dialog-backdrop">
          <div class="web-dialog" role="dialog" aria-modal="true" aria-labelledby="web-dialog-title">
            <h2 id="web-dialog-title">${dialog.title}</h2>
            ${dialog.header ? html`<p><strong>${dialog.header}</strong></p>` : nothing}
            ${dialog.body ? html`<p>${dialog.body}</p>` : nothing}
            ${dialog.showProgress ? html`<progress aria-label="Working"></progress>` : nothing}
            <div class="actions">
              ${dialog.buttons.map((button, index) => html`<button type="button" @click=${() => routeables.answer(dialog, index)}>${button}</button>`)}
            </div>
            ${dialog.footer ? html`<p class="web-dialog-footer">${dialog.footer}</p>` : nothing}
          </div>
        </div>` : nothing}`;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'xa-interactions': Interactions;
  }
}
