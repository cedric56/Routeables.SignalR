<script setup lang="ts">
import { computed } from 'vue';
import type { Page } from '@routeables/client';
import { routeables } from '../routeables';
import type { State } from '../routeables';

const props = withDefaults(defineProps<{ page: Page; target?: string }>(), { target: '' });
const s = computed(() => props.page.state as State);
interface Item { name: string; glyph: string; isCurrent: boolean; select: unknown }
const items = computed(() => (s.value['items'] as Item[] | undefined) ?? []);
</script>

<!-- The menu of the left panel. It is in a region of its own: its commands are called on that region. -->
<template>
  <nav class="rg-menu">
    <div class="rg-menu-moves">
      <button type="button" :disabled="!s.canGoBack" @click="routeables.run(target, s.backCommand)">◀ Back</button>
      <button type="button" :disabled="!s.canGoForward" @click="routeables.run(target, s.forwardCommand)">Forward ▶</button>
    </div>
    <button v-for="item in items" :key="item.name" type="button" class="rg-menu-item" :class="{ current: item.isCurrent }"
            :aria-current="item.isCurrent ? 'page' : undefined" @click="routeables.run(target, item.select)">
      <span class="rg-menu-glyph">{{ item.glyph }}</span>
      <span>{{ item.name }}</span>
    </button>
  </nav>
</template>
