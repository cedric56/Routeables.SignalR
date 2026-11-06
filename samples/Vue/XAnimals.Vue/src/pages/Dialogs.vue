<script setup lang="ts">
import { computed } from 'vue';
import type { Page } from '@routeables/client';
import { routeables } from '../routeables';
import type { State } from '../routeables';

const props = withDefaults(defineProps<{ page: Page; target?: string }>(), { target: '' });
const s = computed(() => props.page.state as State);

interface Action { command: string; label: string }

const groups: { title: string; actions: Action[] }[] = [
  {
    title: 'Messages',
    actions: [
      { command: 'okCommand', label: 'Ok' },
      { command: 'errorCommand', label: 'Error' },
      { command: 'yesNoCommand', label: 'Yes / No' },
      { command: 'okCancelCommand', label: 'Ok / Cancel' },
      { command: 'okAbortCommand', label: 'Ok / Abort' },
      { command: 'yesNoCancelCommand', label: 'Yes / No / Cancel' },
      { command: 'yesNoAbortCommand', label: 'Yes / No / Abort' },
    ],
  },
  {
    title: 'Rich dialogs',
    actions: [
      { command: 'contentDialogCommand', label: 'Content dialog' },
      { command: 'taskDialogCommand', label: 'Task dialog' },
      { command: 'progressDialogCommand', label: 'Task dialog with progress' },
      { command: 'modalCommand', label: 'Show modal' },
    ],
  },
  {
    title: 'Notifications',
    actions: [
      { command: 'informationCommand', label: 'Information' },
      { command: 'successCommand', label: 'Success' },
      { command: 'warningCommand', label: 'Warning' },
      { command: 'notificationErrorCommand', label: 'Error' },
      { command: 'systemNotificationCommand', label: 'System' },
    ],
  },
];

const can = (action: Action): boolean => (s.value[action.command] as { canExecute?: boolean } | undefined)?.canExecute !== false;
</script>

<!-- The buttons that open the dialogs and the notifications. A dialog goes to the browser (see Interactions) and the view model waits
     for the answer on the server, like it waits for a window on a desktop. -->
<template>
  <div class="about-page">
    <section v-for="group in groups" :key="group.title" class="about-group">
      <h2>{{ group.title }}</h2>
      <div class="actions">
        <button v-for="action in group.actions" :key="action.command" type="button" :disabled="!can(action)"
                @click="routeables.run(target, s[action.command])">{{ action.label }}</button>
      </div>
    </section>
    <p v-if="s.hasResult" class="about-result" role="status">{{ s.result }}</p>
  </div>
</template>
