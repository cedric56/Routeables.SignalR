import type { Snapshot, State } from './routeables';
import { routeables } from './routeables';
import { fill, h } from './dom';

/**
 * What the services of the server put on the screen: the notifications, the dialog the user has to answer, and the modal (a view model of
 * the server, drawn here). The view model that asked waits on the server for the answer.
 */
export function interactions(): { node: HTMLElement; update(snapshot: Snapshot): void } {
  const node = h('div', { style: 'display: contents' });
  const toasts = h('div', { style: 'display: contents' });
  const dialogZone = h('div', { style: 'display: contents' });
  node.append(toasts, dialogZone);

  // The modal is kept while it is the same page: its box keeps what is typed, the server answers each key after the next one may be typed.
  let modalSequence = 0;
  let modalButtons: HTMLElement | null = null;
  let shownDialog: string | null = null;

  return {
    node,
    update({ toasts: list, dialogs, modal: modalPage }: Snapshot): void {
      fill(toasts, list.length ? h('div', { class: 'web-toasts', 'aria-live': 'polite' }, list.map(toast =>
        h('div', { class: `web-toast web-toast-${toast.type.toLowerCase()}` },
          h('button', { type: 'button', class: 'web-toast-body', onclick: () => void routeables.dismiss(toast, true) },
            h('strong', null, toast.title), h('span', null, toast.message)),
          h('button', { type: 'button', class: 'web-toast-close', 'aria-label': 'Close', onclick: () => void routeables.dismiss(toast, false) }, '✕')))) : null);

      if (modalPage) {
        const modal = modalPage.state as State;
        if (modalPage.sequence !== modalSequence) {
          modalSequence = modalPage.sequence;
          shownDialog = null;
          modalButtons = h('div', { class: 'actions' });
          fill(dialogZone, h('div', { class: 'web-dialog-backdrop' },
            h('div', { class: 'web-dialog', role: 'dialog', 'aria-modal': 'true', 'aria-labelledby': 'web-dialog-title' },
              h('h2', { id: 'web-dialog-title' }, modal.title),
              h('input', { class: 'search-box', placeholder: 'Write a note', 'aria-label': 'Note', value: modal.note ?? '',
                oninput: (e: Event) => void routeables.set('$modal', 'note', (e.target as HTMLInputElement).value) }),
              modalButtons)));
        }
        fill(modalButtons!, (modal.commands as State[]).map(button =>
          h('button', { type: 'button', disabled: !button.command.canExecute, onclick: () => void routeables.run('$modal', button.command) }, button.name)));
        return;
      }
      modalSequence = 0;

      const dialog = dialogs[0];
      if (dialog?.id === shownDialog) return;
      shownDialog = dialog?.id ?? null;
      fill(dialogZone, dialog ? h('div', { class: 'web-dialog-backdrop' },
        h('div', { class: 'web-dialog', role: 'dialog', 'aria-modal': 'true', 'aria-labelledby': 'web-dialog-title' },
          h('h2', { id: 'web-dialog-title' }, dialog.title),
          dialog.header && h('p', null, h('strong', null, dialog.header)),
          dialog.body && h('p', null, dialog.body),
          dialog.showProgress && h('progress', { 'aria-label': 'Working' }),
          h('div', { class: 'actions' }, dialog.buttons.map((button, index) =>
            h('button', { type: 'button', onclick: () => void routeables.answer(dialog, index) }, button))),
          dialog.footer && h('p', { class: 'web-dialog-footer' }, dialog.footer))) : null);
    },
  };
}
