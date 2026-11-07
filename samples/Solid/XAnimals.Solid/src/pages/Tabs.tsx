import { createSignal, For, onMount } from 'solid-js';
import { routeables, tabs } from '../routeables';
import type { PageProps } from '../store';
import { Region } from '../Region';

/** The items of a tab control are routes: each tab shows its route in the region of the page, and the region draws it. */
export function Tabs(_: PageProps) {
  const [selected, setSelected] = createSignal('');

  function select(route: string): void {
    setSelected(route);
    void routeables.goToIn('tabs-demo', route);
  }

  // A tab control selects its first item when it appears.
  onMount(() => select(tabs[0]!.route));

  return (
    <>
      <div class="about-page">
        <div class="actions" role="tablist">
          <For each={tabs}>
            {tab => (
              <button type="button" role="tab" class="adopt-choice" aria-selected={tab.route === selected()}
                      onClick={() => select(tab.route)}>{tab.header}</button>
            )}
          </For>
        </div>
      </div>
      <div class="about-page">
        <section class="about-group"><Region name="tabs-demo" /></section>
      </div>
    </>
  );
}
