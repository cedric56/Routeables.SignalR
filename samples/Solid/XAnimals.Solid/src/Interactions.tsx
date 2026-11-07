import { createMemo, For, Index, Show } from 'solid-js';
import { routeables } from './routeables';
import type { State } from './routeables';
import { snapshot } from './store';

/**
 * What the services of the server put on the screen: the notifications, the dialog the user has to answer, and the modal (a view model of
 * the server, drawn here). The view model that asked waits on the server for the answer.
 */
export function Interactions() {
  const dialog = createMemo(() => snapshot().dialogs[0]);
  const modal = createMemo(() => snapshot().modal?.state as State | undefined);
  const modalSequence = createMemo(() => snapshot().modal?.sequence);

  return (
    <>
      <Show when={snapshot().toasts.length}>
        <div class="web-toasts" aria-live="polite">
          <For each={snapshot().toasts}>
            {toast => (
              <div class={`web-toast web-toast-${toast.type.toLowerCase()}`}>
                <button type="button" class="web-toast-body" onClick={() => void routeables.dismiss(toast, true)}>
                  <strong>{toast.title}</strong>
                  <span>{toast.message}</span>
                </button>
                <button type="button" class="web-toast-close" aria-label="Close" onClick={() => void routeables.dismiss(toast, false)}>✕</button>
              </div>
            )}
          </For>
        </div>
      </Show>

      <Show when={modalSequence()} keyed fallback={
        <Show when={dialog()} keyed>
          {current => (
            <div class="web-dialog-backdrop">
              <div class="web-dialog" role="dialog" aria-modal="true" aria-labelledby="web-dialog-title">
                <h2 id="web-dialog-title">{current.title}</h2>
                <Show when={current.header}><p><strong>{current.header}</strong></p></Show>
                <Show when={current.body}><p>{current.body}</p></Show>
                <Show when={current.showProgress}><progress aria-label="Working" /></Show>
                <div class="actions">
                  <Index each={current.buttons}>
                    {(button, index) => <button type="button" onClick={() => void routeables.answer(current, index)}>{button()}</button>}
                  </Index>
                </div>
                <Show when={current.footer}><p class="web-dialog-footer">{current.footer}</p></Show>
              </div>
            </div>
          )}
        </Show>
      }>
        {_sequence => (
          <div class="web-dialog-backdrop">
            <div class="web-dialog" role="dialog" aria-modal="true" aria-labelledby="web-dialog-title">
              <h2 id="web-dialog-title">{modal()?.title}</h2>
              {/* The box keeps what is typed (set once): the server answers each key after the next one may already be typed. */}
              <input class="search-box" placeholder="Write a note" aria-label="Note" ref={el => (el.value = modal()?.note ?? '')}
                     onInput={e => void routeables.set('$modal', 'note', e.currentTarget.value)} />
              <div class="actions">
                <For each={(modal()?.commands as State[] | undefined) ?? []}>
                  {button => (
                    <button type="button" disabled={!button.command.canExecute}
                            onClick={() => void routeables.run('$modal', button.command)}>{button.name}</button>
                  )}
                </For>
              </div>
            </div>
          </div>
        )}
      </Show>
    </>
  );
}
