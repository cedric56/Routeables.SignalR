<script setup lang="ts">
import { computed } from 'vue';
import type { Page } from '@routeables/client';
import type { State } from '../routeables';

const props = withDefaults(defineProps<{ page: Page; target?: string }>(), { target: '' });
const s = computed(() => props.page.state as State);
</script>

<template>
  <div class="rg-logo"><img src="/images/logo.svg" :alt="`${s.name} logo`" /></div>
</template>
