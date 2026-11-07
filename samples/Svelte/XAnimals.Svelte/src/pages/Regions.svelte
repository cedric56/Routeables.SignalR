<script lang="ts">
  import type { Page } from '@routeables/client';
  import { routeables } from '../routeables';
  import type { State } from '../routeables';

  let { page, target = '' }: { page: Page; target?: string } = $props();
  const s = $derived(page.state as State);
  import Region from '../Region.svelte';

  const label = $derived(s.isLeftCollapsed ? 'Show the panel' : 'Reduce the panel');
</script>

<!-- An application of its own: four zones, four routers. Each zone shows whatever the router of its region displays. -->
<div class="rg-app" class:left-collapsed={s.isLeftCollapsed}>
  <header class="rg-header"><Region name="shell-header" /></header>
  <aside class="rg-left">
    <button type="button" class="rg-collapse" title={label} aria-label={label} aria-expanded={!s.isLeftCollapsed}
            onclick={() => routeables.run(target, s.toggleLeftCommand)}>{s.isLeftCollapsed ? '»' : '«'}</button>
    <div class="rg-left-region"><Region name="shell-left" /></div>
    <!-- One navigation made of segments: the logo in the content, the author in the footer, the copyright in the left panel. -->
    <button type="button" class="rg-credits" onclick={() => routeables.run(target, s.creditsCommand)}>{s.creditsText}</button>
  </aside>
  <main class="rg-content"><Region name="shell-content" /></main>
  <footer class="rg-footer"><Region name="shell-footer" /></footer>
</div>
