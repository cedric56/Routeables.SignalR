import { For } from 'solid-js';
import { routeables } from '../routeables';
import type { State } from '../routeables';
import type { PageProps } from '../store';

interface Item { name: string; glyph: string; isCurrent: boolean; select: unknown }

/** The menu of the left panel. It is in a region of its own: its commands are called on that region. */
export function LayoutMenu(props: PageProps) {
  const s = (): State => props.page.state;
  const items = (): Item[] => s().items ?? [];
  return (
    <nav class="rg-menu">
      <div class="rg-menu-moves">
        <button type="button" disabled={!s().canGoBack} onClick={() => void routeables.run(props.target, s().backCommand)}>◀ Back</button>
        <button type="button" disabled={!s().canGoForward} onClick={() => void routeables.run(props.target, s().forwardCommand)}>Forward ▶</button>
      </div>
      <For each={items()}>
        {item => (
          <button type="button" class="rg-menu-item" classList={{ current: item.isCurrent }}
                  aria-current={item.isCurrent ? 'page' : undefined} onClick={() => void routeables.run(props.target, item.select)}>
            <span class="rg-menu-glyph">{item.glyph}</span>
            <span>{item.name}</span>
          </button>
        )}
      </For>
    </nav>
  );
}
