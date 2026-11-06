<script setup lang="ts">
import { computed } from 'vue';
import type { Page } from '@routeables/client';
import { routeables } from '../routeables';
import type { State } from '../routeables';

const props = withDefaults(defineProps<{ page: Page; target?: string }>(), { target: '' });
const s = computed(() => props.page.state as State);
import { ref } from 'vue';
import Region from '../Region.vue';

const tabs = [
  { header: 'Cats', route: 'cats' },
  { header: 'Dogs', route: 'dogs' },
  { header: 'Monkeys', route: 'monkeys' },
];
const selected = ref('');

function select(route: string): void {
  selected.value = route;
  void routeables.goToIn('tabs-demo', route);
}

// A tab control selects its first item when it appears.
select(tabs[0].route);
</script>

<!-- The items of a tab control are routes: each tab shows its route in the region of the page, and the region draws it. -->
<template>
  <div class="about-page">
    <div class="actions" role="tablist">
      <button v-for="tab in tabs" :key="tab.route" type="button" role="tab" class="adopt-choice"
              :aria-selected="tab.route === selected" @click="select(tab.route)">{{ tab.header }}</button>
    </div>
  </div>
  <div class="about-page">
    <section class="about-group"><Region name="tabs-demo" /></section>
  </div>
</template>
