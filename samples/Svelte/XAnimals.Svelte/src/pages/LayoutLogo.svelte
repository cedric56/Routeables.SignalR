<script lang="ts">
  import type { Page } from '@routeables/client';
  import type { State } from '../routeables';

  let { page }: { page: Page; target?: string } = $props();
  const s = $derived(page.state as State);
</script>

<div class="rg-logo"><img src="/images/logo.svg" alt={`${s.name} logo`} /></div>
