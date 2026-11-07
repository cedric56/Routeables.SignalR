<script lang="ts">
  import type { Page } from '@routeables/client';
  import { firstSegment, routeables } from '../routeables';
  import type { State } from '../routeables';

  let { page }: { page: Page; target?: string } = $props();
  const s = $derived(page.state as State);

  interface Animal { name: string; location: string; details: string; photoUrl: string }

  const animal = $derived(s.animal as Animal | null | undefined);
  const list = $derived('/' + firstSegment(page.route));

  function back(event: Event): void {
    event.preventDefault();
    void routeables.goTo(list);
  }
</script>

<div class="monkey-detail">
  {#if animal}
    <h1 class="title">{animal.name}</h1>
    <p class="location">{animal.location}</p>
    <img src={animal.photoUrl} alt={animal.name} width="260" height="330" />
    <p class="body-text">{animal.details}</p>
  {:else}
    <h1 class="title">Animal not found</h1>
    <p class="body-text">This animal was not found.</p>
  {/if}
  <a class="back-btn" href={list} onclick={back}>Back to list</a>
</div>
