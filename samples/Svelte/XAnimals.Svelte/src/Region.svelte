<script lang="ts">
  import type { Page } from '@routeables/client';
  import { routeables } from './routeables';
  import PageView from './PageView.svelte';

  /**
   * The zone of a region: it shows what the router of the region displays. Nothing assigns a page to it; the page is chosen by its
   * view model. A page that arrives plays the transition of the zone again (the key is the number of the page).
   */
  let { name, transition = 'None' }: { name: string; transition?: string } = $props();

  let page = $state.raw<Page | null>(null);
  $effect(() => routeables.follow(name, next => (page = next)));
</script>

{#if page}
  {#key page.sequence}
    <div class={`rt-enter rt-${transition.toLowerCase()}`}>
      <PageView {page} target={name} />
    </div>
  {/key}
{/if}
