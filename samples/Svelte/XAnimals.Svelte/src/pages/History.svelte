<script lang="ts">
  import type { Page } from '@routeables/client';
  import { routeables } from '../routeables';
  import type { State } from '../routeables';

  let { page, target = '' }: { page: Page; target?: string } = $props();
  const s = $derived(page.state as State);
  import Region from '../Region.svelte';

  interface Entry { number: number; name: string; glyph: string; caption: string; badge: string; isCurrent: boolean; isAhead: boolean }

  const entries = $derived((s.entries as Entry[] | undefined) ?? []);
  const transitions = $derived((s.transitions as string[] | undefined) ?? []);
  const run = (command: string): void => void routeables.run(target, s[command]);
</script>

<!-- The history of a region, as cards, and the frame that shows the page the region displays. -->
<div class="about-page">
  <section class="about-group">
    <div class="nav-track" role="list">
      {#each entries as entry (entry.number)}
        <div class="nav-card" role="listitem" class:current={entry.isCurrent} class:ahead={entry.isAhead} class:behind={!entry.isCurrent && !entry.isAhead}>
          <span class="nav-number">{entry.number}</span>
          <span class="nav-badge">{entry.badge}</span>
          <span class="nav-glyph">{entry.glyph}</span>
          <span class="nav-name">{entry.name}</span>
          <span class="nav-caption">{entry.caption}</span>
        </div>
      {/each}
    </div>
  </section>

  <section class="about-group">
    <!-- The zone of the "history" region: nothing assigns the page, it shows what the router of the region displays. -->
    <div class="nav-frame" inert><Region name="history" transition={s.transition} /></div>
  </section>

  <section class="about-group">
    <div class="actions">
      <button type="button" onclick={() => run('pushCommand')}>＋ Push</button>
      <button type="button" disabled={!s.canGoBack} onclick={() => run('backCommand')}>◀ Back</button>
      <button type="button" disabled={!s.canGoForward} onclick={() => run('forwardCommand')}>Forward ▶</button>
      <button type="button" disabled={!s.canGoBack} onclick={() => run('popCommand')}>↩ Pop</button>
    </div>
  </section>

  <section class="about-group">
    <h2>Transition</h2>
    <select aria-label="Transition" value={s.transition} onchange={e => routeables.set(target, 'transition', e.currentTarget.value)}>
      {#each transitions as name (name)}
        <option value={name}>{name}</option>
      {/each}
    </select>
  </section>

  <section class="about-group">
    <h2>CanLeaveAsync</h2>
    <button type="button" class="nav-check" onclick={() => routeables.set(target, 'blockLeaving', !s.blockLeaving)}>{s.guardGlyph} {s.guardText}</button>
  </section>

  <p class="about-result nav-status" class:refused={s.isRefused} role="status">{s.isRefused ? '🚫 ' : ''}{s.status}</p>
</div>
