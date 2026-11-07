import { createMemo, Show } from 'solid-js';
import { useTarget } from './store';
import { PageView } from './PageView';

/**
 * The zone of a region: it shows what the router of the region displays. Nothing assigns a page to it; the page is chosen by its
 * view model. A page that arrives plays the transition of the zone again (keyed by the number of the page).
 */
export function Region(props: { name: string; transition?: string }) {
  const page = useTarget(() => props.name);
  const sequence = createMemo(() => page()?.sequence);

  return (
    <Show when={sequence()} keyed>
      {_sequence => (
        <div class={`rt-enter rt-${(props.transition ?? 'None').toLowerCase()}`}>
          <PageView page={page()!} target={props.name} />
        </div>
      )}
    </Show>
  );
}
