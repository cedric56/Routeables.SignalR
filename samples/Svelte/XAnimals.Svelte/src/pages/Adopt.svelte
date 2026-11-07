<script lang="ts">
  import type { Page } from '@routeables/client';
  import { routeables } from '../routeables';
  import type { State } from '../routeables';

  let { page, target = '' }: { page: Page; target?: string } = $props();
  const s = $derived(page.state as State);

  interface Pet { animal: { photoUrl: string }; name: string; location: string; glyph: string }
  interface Choice { name: string; title: string; adoptCommand: unknown }

  // The cards are the history of the region of the adopted animals (`$pets`).
  let petsPage = $state.raw<Page | null>(null);
  $effect(() => routeables.follow('$pets', next => (petsPage = next)));
  const choices = $derived((s.choices as Choice[] | undefined) ?? []);
  const pets = $derived((petsPage?.state['pets'] as Pet[] | undefined) ?? []);
</script>

<!-- One button per animal: adopting it routes its card into the region "adopt". -->
<div class="about-page">
  <section class="about-group adopt-choices">
    {#each choices as choice (choice.name)}
      <button type="button" class="adopt-choice" onclick={() => routeables.run(target, choice.adoptCommand)}>{choice.title}</button>
    {/each}
  </section>

  <section class="about-group adopt-bar">
    <p class="nav-status" role="status">{s.status}</p>
    <button type="button" class="adopt-choice" onclick={() => routeables.run(target, s.releaseCommand)}>Release the last</button>
    <button type="button" class="adopt-choice" onclick={() => routeables.run(target, s.releaseAllCommand)}>Release all</button>
  </section>

  <div class="adopt-zone" style="display: flex; flex-wrap: wrap; gap: 0.8rem">
    {#each pets as pet (pet.name)}
      <article class="pet-card">
        <img src={pet.animal.photoUrl} alt={pet.name} width="150" height="190" />
        <strong>{pet.name}</strong>
        <span class="pet-location">{pet.location}</span>
        <span class="pet-glyph">{pet.glyph}</span>
      </article>
    {/each}
  </div>
</div>
