import { Component, computed, inject, input } from '@angular/core';
import type { Page } from '@routeables/client';
import { Routeables } from '../routeables';
import type { State } from '../routeables';

interface Action {
  command: string;
  label: string;
}

/**
 * The buttons that open the dialogs and the notifications. A dialog goes to the browser (see Interactions) and the view model waits
 * for the answer on the server, like it waits for a window on a desktop.
 */
@Component({
  selector: 'app-dialogs',
  template: `
    <div class="about-page">
      @for (group of groups; track group.title) {
        <section class="about-group">
          <h2>{{ group.title }}</h2>
          <div class="actions">
            @for (action of group.actions; track action.command) {
              <button type="button" [disabled]="!can(action)" (click)="routeables.run(target(), s()[action.command])">{{ action.label }}</button>
            }
          </div>
        </section>
      }
      @if (s().hasResult) { <p class="about-result" role="status">{{ s().result }}</p> }
    </div>
  `,
})
export class DialogsPage {
  readonly page = input.required<Page>();
  readonly target = input('');
  protected readonly routeables = inject(Routeables);
  protected readonly s = computed(() => this.page().state as State);

  protected readonly groups: { title: string; actions: Action[] }[] = [
    {
      title: 'Messages',
      actions: [
        { command: 'okCommand', label: 'Ok' },
        { command: 'errorCommand', label: 'Error' },
        { command: 'yesNoCommand', label: 'Yes / No' },
        { command: 'okCancelCommand', label: 'Ok / Cancel' },
        { command: 'okAbortCommand', label: 'Ok / Abort' },
        { command: 'yesNoCancelCommand', label: 'Yes / No / Cancel' },
        { command: 'yesNoAbortCommand', label: 'Yes / No / Abort' },
      ],
    },
    {
      title: 'Rich dialogs',
      actions: [
        { command: 'contentDialogCommand', label: 'Content dialog' },
        { command: 'taskDialogCommand', label: 'Task dialog' },
        { command: 'progressDialogCommand', label: 'Task dialog with progress' },
        { command: 'modalCommand', label: 'Show modal' },
      ],
    },
    {
      title: 'Notifications',
      actions: [
        { command: 'informationCommand', label: 'Information' },
        { command: 'successCommand', label: 'Success' },
        { command: 'warningCommand', label: 'Warning' },
        { command: 'notificationErrorCommand', label: 'Error' },
        { command: 'systemNotificationCommand', label: 'System' },
      ],
    },
  ];

  protected can(action: Action): boolean {
    return (this.s()[action.command] as { canExecute?: boolean } | undefined)?.canExecute !== false;
  }
}
