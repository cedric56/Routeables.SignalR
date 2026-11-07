<script lang="ts">
  import type { Page } from '@routeables/client';
  import type { State } from '../routeables';

  let { page }: { page: Page; target?: string } = $props();
  const s = $derived(page.state as State);
</script>

<div class="rg-bar" class:strong={s.isStrong}>
  <span class="rg-bar-glyph">{s.glyph}</span>
  <div class="rg-bar-text">
    <strong>{s.title}</strong>
    <span>{s.detail}</span>
  </div>
</div>
