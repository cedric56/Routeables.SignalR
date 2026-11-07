<script lang="ts">
  import type { Page } from '@routeables/client';
  import { firstSegment, routeables } from '../routeables';
  import type { State } from '../routeables';

  let { page, target = '' }: { page: Page; target?: string } = $props();
  const s = $derived(page.state as State);

  interface Animal { name: string; location: string; details: string; thumbnailUrl: string; photoUrl: string }

  // The first segment of the address of a page: /cats/Abyssinian is the page of the cats.
  const kind = $derived(firstSegment(page.route));
  const title = $derived(kind.charAt(0).toUpperCase() + kind.slice(1));
  const animals = $derived((s.filteredAnimals as Animal[] | undefined) ?? []);

  const detail = (animal: Animal): string => `/${kind}/${encodeURIComponent(animal.name)}`;

  // A link is a route: the server navigates and the page that arrives replaces this one. No reload.
  function open(event: Event, animal: Animal): void {
    event.preventDefault();
    void routeables.goTo(detail(animal));
  }
</script>

{#snippet item(animal: Animal)}
  <img src={animal.thumbnailUrl} alt={animal.name} width="56" height="68" loading="lazy" />
  <span class="animal-text">
    <span class="animal-name">{animal.name}</span>
    <span class="animal-location">{animal.location}</span>
  </span>
{/snippet}

<!-- The list of a species. A list that a region shows (hostsDetail is false) does not leave the page: no search, no link to a detail. -->
<div class="animals-page">
  <h1>{title}</h1>
  {#if s.hostsDetail}
    <div class="search-row">
      <!-- The box keeps what is typed (defaultValue): the server answers each key after the next one may already be typed. -->
      <input class="search-box" type="search" placeholder="Enter search term" aria-label="Search animals"
             defaultValue={s.searchText ?? ''} oninput={e => routeables.set(target, 'searchText', e.currentTarget.value)} />
    </div>
  {/if}
  {#if !animals.length}<p class="no-results">No {kind} found.</p>{/if}
  <div class="animal-list">
    {#each animals as animal (animal.name)}
      {#if s.hostsDetail}
        <a class="animal-item" href={detail(animal)} onclick={e => open(e, animal)}>{@render item(animal)}</a>
      {:else}
        <div class="animal-item">{@render item(animal)}</div>
      {/if}
    {/each}
  </div>
</div>

<style>
  .search-row { display: flex; gap: 0.5rem; align-items: center; }
  .search-row .search-box { flex: 1; }
</style>
