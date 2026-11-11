<script setup lang="ts">
import { computed } from 'vue';
import type { Page } from '@routeables/client';
import { routeables } from '../routeables';
import type { State } from '../routeables';

/**
 * The Essentials page: the server calls the device APIs of this browser (the ones `browserEssentials()` gives) with the .NET MAUI
 * contracts, and shows what came back. The page only draws the list: every test runs on the server.
 */
interface Check {
  group: string;
  name: string;
  description: string;
  label: string;
  supported: boolean;
  status: string;
  result: string;
  runCommand: unknown;
}

const props = withDefaults(defineProps<{ page: Page; target?: string }>(), { target: '' });
const s = computed(() => props.page.state as State);

// The tests by group, in the order of the server.
const groups = computed(() => {
  const groups: { name: string; checks: Check[] }[] = [];
  for (const check of (s.value.checks ?? []) as Check[]) {
    const group = groups.find(g => g.name === check.group);
    if (group) group.checks.push(check);
    else groups.push({ name: check.group, checks: [check] });
  }
  return groups;
});

const run = (check: Check): void => void routeables.run(props.target, check.runCommand);
</script>

<template>
  <div class="about-page essentials-page">
    <p class="essentials-summary" role="status">{{ s.summary }}</p>
    <ul v-if="s.capabilities?.length" class="essentials-capabilities" aria-label="Announced services">
      <li v-for="capability in s.capabilities" :key="capability">{{ capability }}</li>
    </ul>
    <section v-for="group in groups" :key="group.name" class="essentials-group">
      <h2>{{ group.name }}</h2>
      <article v-for="check in group.checks" :key="check.name" class="essentials-check" :class="{ unsupported: !check.supported }">
        <span class="essentials-name">
          {{ check.name }}
          <span class="essentials-status" :class="check.status">{{ check.status }}</span>
        </span>
        <p class="essentials-description">{{ check.description }}</p>
        <button type="button" :disabled="!check.supported || check.status === 'running'" @click="run(check)">{{ check.label }}</button>
        <pre v-if="check.result" class="essentials-result">{{ check.result }}</pre>
      </article>
    </section>
  </div>
</template>
