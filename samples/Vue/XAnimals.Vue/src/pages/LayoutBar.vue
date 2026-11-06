<script setup lang="ts">
import { computed } from 'vue';
import type { Page } from '@routeables/client';
import type { State } from '../routeables';

const props = withDefaults(defineProps<{ page: Page; target?: string }>(), { target: '' });
const s = computed(() => props.page.state as State);
</script>

<template>
  <div class="rg-bar" :class="{ strong: s.isStrong }">
    <span class="rg-bar-glyph">{{ s.glyph }}</span>
    <div class="rg-bar-text">
      <strong>{{ s.title }}</strong>
      <span>{{ s.detail }}</span>
    </div>
  </div>
</template>
