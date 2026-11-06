import { Component, computed, inject } from '@angular/core';
import type { Dialog, Toast } from '@routeables/client';
import { Routeables } from './routeables';
import type { State } from './routeables';

/**
 * What the services of the server put on the screen: the notifications, the dialog the user has to answer, and the modal (a view model of
 * the server, drawn here). The view model that asked waits on the server for the answer.
 */
@Component({
  selector: 'app-interactions',
  template: `
    @if (routeables.toasts().length) {
      <div class="web-toasts" aria-live="polite">
        @for (toast of routeables.toasts(); track toast.id) {
          <div [class]="'web-toast web-toast-' + toast.type.toLowerCase()">
            <button type="button" class="web-toast-body" (click)="routeables.dismiss(toast, true)">
              <strong>{{ toast.title }}</strong>
              <span>{{ toast.message }}</span>
            </button>
            <button type="button" class="web-toast-close" aria-label="Close" (click)="routeables.dismiss(toast, false)">✕</button>
          </div>
        }
      </div>
    }

    @if (modal(); as note) {
      <div class="web-dialog-backdrop">
        <div class="web-dialog" role="dialog" aria-modal="true" aria-labelledby="web-dialog-title">
          <h2 id="web-dialog-title">{{ note.title }}</h2>
          <input class="search-box" placeholder="Write a note" aria-label="Note" [value]="note.note" (input)="write($event)" />
          <div class="actions">
            @for (button of note.commands; track button.name) {
              <button type="button" [disabled]="!button.command.canExecute" (click)="routeables.run('$modal', button.command)">{{ button.name }}</button>
            }
          </div>
        </div>
      </div>
    } @else if (dialog(); as d) {
      <div class="web-dialog-backdrop">
        <div class="web-dialog" role="dialog" aria-modal="true" aria-labelledby="web-dialog-title">
          <h2 id="web-dialog-title">{{ d.title }}</h2>
          @if (d.header) { <p><strong>{{ d.header }}</strong></p> }
          @if (d.body) { <p>{{ d.body }}</p> }
          @if (d.showProgress) { <progress aria-label="Working"></progress> }
          <div class="actions">
            @for (button of d.buttons; track $index) {
              <button type="button" (click)="routeables.answer(d, $index)">{{ button }}</button>
            }
          </div>
          @if (d.footer) { <p class="web-dialog-footer">{{ d.footer }}</p> }
        </div>
      </div>
    }
  `,
})
export class Interactions {
  protected readonly routeables = inject(Routeables);
  protected readonly dialog = computed<Dialog | undefined>(() => this.routeables.dialogs()[0]);
  protected readonly modal = computed(() => this.routeables.modal()?.state as State | undefined);

  protected write(event: Event): void {
    void this.routeables.set('$modal', 'note', (event.target as HTMLInputElement).value);
  }
}

export type { Toast };
