import { For, Show } from 'solid-js';
import { canRun, dialogGroups, routeables } from '../routeables';
import type { State } from '../routeables';
import type { PageProps } from '../store';

/**
 * The buttons that open the dialogs and the notifications. A dialog goes to the browser (see Interactions) and the view model waits
 * for the answer on the server, like it waits for a window on a desktop.
 */
export function Dialogs(props: PageProps) {
  const s = (): State => props.page.state;
  return (
    <div class="about-page">
      <For each={dialogGroups}>
        {group => (
          <section class="about-group">
            <h2>{group.title}</h2>
            <div class="actions">
              <For each={group.actions}>
                {action => (
                  <button type="button" disabled={!canRun(s()[action.command])}
                          onClick={() => void routeables.run(props.target, s()[action.command])}>{action.label}</button>
                )}
              </For>
            </div>
          </section>
        )}
      </For>
      <Show when={s().hasResult}><p class="about-result" role="status">{s().result}</p></Show>
    </div>
  );
}
