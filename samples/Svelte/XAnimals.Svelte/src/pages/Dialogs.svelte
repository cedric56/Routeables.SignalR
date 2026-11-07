<script lang="ts">
  import type { Page } from '@routeables/client';
  import { canRun, dialogGroups, routeables } from '../routeables';
  import type { State } from '../routeables';

  let { page, target = '' }: { page: Page; target?: string } = $props();
  const s = $derived(page.state as State);
</script>

<!-- The buttons that open the dialogs and the notifications. A dialog goes to the browser (see Interactions) and the view model waits
     for the answer on the server, like it waits for a window on a desktop. -->
<div class="about-page">
  {#each dialogGroups as group (group.title)}
    <section class="about-group">
      <h2>{group.title}</h2>
      <div class="actions">
        {#each group.actions as action (action.command)}
          <button type="button" disabled={!canRun(s[action.command])} onclick={() => routeables.run(target, s[action.command])}>{action.label}</button>
        {/each}
      </div>
    </section>
  {/each}
  {#if s.hasResult}<p class="about-result" role="status">{s.result}</p>{/if}
</div>
