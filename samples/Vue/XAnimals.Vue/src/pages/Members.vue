<script setup lang="ts">
import { computed } from 'vue';
import type { Page } from '@routeables/client';
import { routeables } from '../routeables';
import type { State } from '../routeables';

const props = withDefaults(defineProps<{ page: Page; target?: string }>(), { target: '' });
const s = computed(() => props.page.state as State);
</script>

<template>
  <div class="about-page">
    <div class="about-group">
      <p>{{ s.welcome }}</p>
      <div class="actions">
        <button type="button" @click="routeables.run(target, s.signOutCommand)">Sign out</button>
      </div>
    </div>
  </div>
</template>
