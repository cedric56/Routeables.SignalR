<script setup lang="ts">
import { computed } from 'vue';
import type { Page } from '@routeables/client';
import { routeables } from '../routeables';
import type { State } from '../routeables';

const props = withDefaults(defineProps<{ page: Page; target?: string }>(), { target: '' });
const s = computed(() => props.page.state as State);
import Region from '../Region.vue';

const toggleLeft = (): void => void routeables.run(props.target, s.value['toggleLeftCommand']);
</script>

<!-- An application of its own: four zones, four routers. Each zone shows whatever the router of its region displays. -->
<template>
  <div class="rg-app" :class="{ 'left-collapsed': s.isLeftCollapsed }">
    <header class="rg-header"><Region name="shell-header" /></header>
    <aside class="rg-left">
      <button type="button" class="rg-collapse" :title="s.isLeftCollapsed ? 'Show the panel' : 'Reduce the panel'"
              :aria-label="s.isLeftCollapsed ? 'Show the panel' : 'Reduce the panel'" :aria-expanded="!s.isLeftCollapsed"
              @click="toggleLeft">{{ s.isLeftCollapsed ? '»' : '«' }}</button>
      <div class="rg-left-region"><Region name="shell-left" /></div>
      <!-- One navigation made of segments: the logo in the content, the author in the footer, the copyright in the left panel. -->
      <button type="button" class="rg-credits" @click="routeables.run(target, s.creditsCommand)">{{ s.creditsText }}</button>
    </aside>
    <main class="rg-content"><Region name="shell-content" /></main>
    <footer class="rg-footer"><Region name="shell-footer" /></footer>
  </div>
</template>
