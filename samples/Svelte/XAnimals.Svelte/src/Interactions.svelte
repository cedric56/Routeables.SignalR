<script lang="ts">
  import { routeables } from './routeables';
  import type { State } from './routeables';
  import { app } from './app.svelte';

  /**
   * What the services of the server put on the screen: the notifications, the dialog the user has to answer, and the modal (a view model of
   * the server, drawn here). The view model that asked waits on the server for the answer.
   */
  const dialog = $derived(app.snapshot.dialogs[0]);
  const modal = $derived(app.snapshot.modal?.state as State | undefined);
</script>

{#if app.snapshot.toasts.length}
  <div class="web-toasts" aria-live="polite">
    {#each app.snapshot.toasts as toast (toast.id)}
      <div class={`web-toast web-toast-${toast.type.toLowerCase()}`}>
        <button type="button" class="web-toast-body" onclick={() => routeables.dismiss(toast, true)}>
          <strong>{toast.title}</strong>
          <span>{toast.message}</span>
        </button>
        <button type="button" class="web-toast-close" aria-label="Close" onclick={() => routeables.dismiss(toast, false)}>✕</button>
      </div>
    {/each}
  </div>
{/if}

{#if modal}
  <div class="web-dialog-backdrop">
    <div class="web-dialog" role="dialog" aria-modal="true" aria-labelledby="web-dialog-title">
      <h2 id="web-dialog-title">{modal.title}</h2>
      <!-- The box keeps what is typed (defaultValue): the server answers each key after the next one may already be typed. -->
      <input class="search-box" placeholder="Write a note" aria-label="Note" defaultValue={modal.note ?? ''}
             oninput={e => routeables.set('$modal', 'note', e.currentTarget.value)} />
      <div class="actions">
        {#each modal.commands as button (button.name)}
          <button type="button" disabled={!button.command.canExecute} onclick={() => routeables.run('$modal', button.command)}>{button.name}</button>
        {/each}
      </div>
    </div>
  </div>
{:else if dialog}
  <div class="web-dialog-backdrop">
    <div class="web-dialog" role="dialog" aria-modal="true" aria-labelledby="web-dialog-title">
      <h2 id="web-dialog-title">{dialog.title}</h2>
      {#if dialog.header}<p><strong>{dialog.header}</strong></p>{/if}
      {#if dialog.body}<p>{dialog.body}</p>{/if}
      {#if dialog.showProgress}<progress aria-label="Working"></progress>{/if}
      <div class="actions">
        {#each dialog.buttons as button, index (index)}
          <button type="button" onclick={() => routeables.answer(dialog, index)}>{button}</button>
        {/each}
      </div>
      {#if dialog.footer}<p class="web-dialog-footer">{dialog.footer}</p>{/if}
    </div>
  </div>
{/if}
