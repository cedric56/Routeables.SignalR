<script lang="ts">
  import type { Page } from '@routeables/client';
  import { routeables } from '../routeables';
  import type { State } from '../routeables';

  let { page, target = '' }: { page: Page; target?: string } = $props();
  const s = $derived(page.state as State);
</script>

<div class="about-page">
  <div class="about-group">
    <p>{s.welcome}</p>
    <div class="actions">
      <button type="button" onclick={() => routeables.run(target, s.signOutCommand)}>Sign out</button>
    </div>
  </div>
</div>
