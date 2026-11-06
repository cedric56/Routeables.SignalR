<script setup lang="ts">
import { defineAsyncComponent, shallowRef, watchEffect } from 'vue';
import type { Page } from '@routeables/client';
import { routeables } from './routeables';

/**
 * The zone of a region: it shows what the router of the region displays. Nothing assigns a page to it; the page is chosen by its
 * view model. A page that arrives plays the transition of the zone again.
 */
const props = withDefaults(defineProps<{
  /** The name of the region. */
  name: string;
  /** The transition the pages come in with (None, Fade, Slide...). */
  transition?: string;
}>(), { transition: 'None' });

// PageView draws pages that contain regions (History, Regions, Tabs): the two components need each other.
const PageView = defineAsyncComponent(() => import('./PageView.vue'));

const page = shallowRef<Page | null>(null);
watchEffect(onCleanup => onCleanup(routeables.watch(props.name, p => (page.value = p))));
</script>

<template>
  <!-- The key is the number of the page: another page replaces the element, and its transition plays again. -->
  <div v-if="page" :key="page.sequence" :class="`rt-enter rt-${transition.toLowerCase()}`">
    <PageView :page="page" :target="name" />
  </div>
</template>
