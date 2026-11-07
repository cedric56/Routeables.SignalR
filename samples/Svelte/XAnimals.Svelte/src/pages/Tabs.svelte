<script lang="ts">
  import type { Page } from '@routeables/client';
  import { routeables, tabs } from '../routeables';
  import Region from '../Region.svelte';

  let {}: { page: Page; target?: string } = $props();

  let selected = $state('');

  function select(route: string): void {
    selected = route;
    void routeables.goToIn('tabs-demo', route);
  }

  // A tab control selects its first item when it appears.
  $effect(() => select(tabs[0]!.route));
</script>

<!-- The items of a tab control are routes: each tab shows its route in the region of the page, and the region draws it. -->
<div class="about-page">
  <div class="actions" role="tablist">
    {#each tabs as tab (tab.route)}
      <button type="button" role="tab" class="adopt-choice" aria-selected={tab.route === selected} onclick={() => select(tab.route)}>{tab.header}</button>
    {/each}
  </div>
</div>
<div class="about-page">
  <section class="about-group"><Region name="tabs-demo" /></section>
</div>
