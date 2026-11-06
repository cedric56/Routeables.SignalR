<script setup lang="ts">
import { computed } from 'vue';
import type { Page } from '@routeables/client';
import { routeables } from '../routeables';
import type { State } from '../routeables';

const props = withDefaults(defineProps<{ page: Page; target?: string }>(), { target: '' });
const s = computed(() => props.page.state as State);
import Region from '../Region.vue';

interface Entry { number: number; name: string; glyph: string; caption: string; badge: string; isCurrent: boolean; isAhead: boolean }

const entries = computed(() => (s.value['entries'] as Entry[] | undefined) ?? []);
const transitions = computed(() => (s.value['transitions'] as string[] | undefined) ?? []);

const run = (command: string): void => void routeables.run(props.target, s.value[command]);
const transition = (event: Event): void => void routeables.set(props.target, 'transition', (event.target as HTMLSelectElement).value);
const block = (): void => void routeables.set(props.target, 'blockLeaving', !s.value['blockLeaving']);
</script>

<!-- The history of a region, as cards, and the frame that shows the page the region displays. -->
<template>
  <div class="about-page">
    <section class="about-group">
      <div class="nav-track" role="list">
        <div v-for="entry in entries" :key="entry.number" class="nav-card" role="listitem"
             :class="{ current: entry.isCurrent, ahead: entry.isAhead, behind: !entry.isCurrent && !entry.isAhead }">
          <span class="nav-number">{{ entry.number }}</span>
          <span class="nav-badge">{{ entry.badge }}</span>
          <span class="nav-glyph">{{ entry.glyph }}</span>
          <span class="nav-name">{{ entry.name }}</span>
          <span class="nav-caption">{{ entry.caption }}</span>
        </div>
      </div>
    </section>

    <section class="about-group">
      <!-- The zone of the "history" region: nothing assigns the page, it shows what the router of the region displays. -->
      <div class="nav-frame" inert><Region name="history" :transition="s.transition" /></div>
    </section>

    <section class="about-group">
      <div class="actions">
        <button type="button" @click="run('pushCommand')">＋ Push</button>
        <button type="button" :disabled="!s.canGoBack" @click="run('backCommand')">◀ Back</button>
        <button type="button" :disabled="!s.canGoForward" @click="run('forwardCommand')">Forward ▶</button>
        <button type="button" :disabled="!s.canGoBack" @click="run('popCommand')">↩ Pop</button>
      </div>
    </section>

    <section class="about-group">
      <h2>Transition</h2>
      <select aria-label="Transition" :value="s.transition" @change="transition">
        <option v-for="name in transitions" :key="name" :value="name">{{ name }}</option>
      </select>
    </section>

    <section class="about-group">
      <h2>CanLeaveAsync</h2>
      <button type="button" class="nav-check" @click="block">{{ s.guardGlyph }} {{ s.guardText }}</button>
    </section>

    <p class="about-result nav-status" :class="{ refused: s.isRefused }" role="status">{{ s.isRefused ? '🚫 ' : '' }}{{ s.status }}</p>
  </div>
</template>
