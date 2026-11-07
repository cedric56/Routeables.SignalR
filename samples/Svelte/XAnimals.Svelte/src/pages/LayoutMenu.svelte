<script lang="ts">
  import type { Page } from '@routeables/client';
  import { routeables } from '../routeables';
  import type { State } from '../routeables';

  let { page, target = '' }: { page: Page; target?: string } = $props();
  const s = $derived(page.state as State);

  interface Item { name: string; glyph: string; isCurrent: boolean; select: unknown }
  const items = $derived((s.items as Item[] | undefined) ?? []);
</script>

<!-- The menu of the left panel. It is in a region of its own: its commands are called on that region. -->
<nav class="rg-menu">
  <div class="rg-menu-moves">
    <button type="button" disabled={!s.canGoBack} onclick={() => routeables.run(target, s.backCommand)}>◀ Back</button>
    <button type="button" disabled={!s.canGoForward} onclick={() => routeables.run(target, s.forwardCommand)}>Forward ▶</button>
  </div>
  {#each items as item (item.name)}
    <button type="button" class="rg-menu-item" class:current={item.isCurrent} aria-current={item.isCurrent ? 'page' : undefined}
            onclick={() => routeables.run(target, item.select)}>
      <span class="rg-menu-glyph">{item.glyph}</span>
      <span>{item.name}</span>
    </button>
  {/each}
</nav>
