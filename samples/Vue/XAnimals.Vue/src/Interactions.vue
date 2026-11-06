<script setup lang="ts">
import { computed } from 'vue';
import { routeables } from './routeables';
import type { State } from './routeables';

/**
 * What the services of the server put on the screen: the notifications, the dialog the user has to answer, and the modal (a view model of
 * the server, drawn here). The view model that asked waits on the server for the answer.
 */
const dialog = computed(() => routeables.dialogs.value[0]);
const modal = computed(() => routeables.modal.value?.state as State | undefined);

const write = (event: Event): void => void routeables.set('$modal', 'note', (event.target as HTMLInputElement).value);
</script>

<template>
  <div v-if="routeables.toasts.value.length" class="web-toasts" aria-live="polite">
    <div v-for="toast in routeables.toasts.value" :key="toast.id" :class="`web-toast web-toast-${toast.type.toLowerCase()}`">
      <button type="button" class="web-toast-body" @click="routeables.dismiss(toast, true)">
        <strong>{{ toast.title }}</strong>
        <span>{{ toast.message }}</span>
      </button>
      <button type="button" class="web-toast-close" aria-label="Close" @click="routeables.dismiss(toast, false)">✕</button>
    </div>
  </div>

  <div v-if="modal" class="web-dialog-backdrop">
    <div class="web-dialog" role="dialog" aria-modal="true" aria-labelledby="web-dialog-title">
      <h2 id="web-dialog-title">{{ modal.title }}</h2>
      <input class="search-box" placeholder="Write a note" aria-label="Note" :value="modal.note" @input="write" />
      <div class="actions">
        <button v-for="button in modal.commands" :key="button.name" type="button" :disabled="!button.command.canExecute"
                @click="routeables.run('$modal', button.command)">{{ button.name }}</button>
      </div>
    </div>
  </div>
  <div v-else-if="dialog" class="web-dialog-backdrop">
    <div class="web-dialog" role="dialog" aria-modal="true" aria-labelledby="web-dialog-title">
      <h2 id="web-dialog-title">{{ dialog.title }}</h2>
      <p v-if="dialog.header"><strong>{{ dialog.header }}</strong></p>
      <p v-if="dialog.body">{{ dialog.body }}</p>
      <progress v-if="dialog.showProgress" aria-label="Working"></progress>
      <div class="actions">
        <button v-for="(button, index) in dialog.buttons" :key="index" type="button" @click="routeables.answer(dialog, index)">{{ button }}</button>
      </div>
      <p v-if="dialog.footer" class="web-dialog-footer">{{ dialog.footer }}</p>
    </div>
  </div>
</template>
