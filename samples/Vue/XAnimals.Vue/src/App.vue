<script setup lang="ts">
import { computed, watchEffect } from 'vue';
import Interactions from './Interactions.vue';
import PageView from './PageView.vue';
import { routeables } from './routeables';
import type { State } from './routeables';

interface MenuEntry {
  name: string;
  route: string;
  glyph: string;
  description: string;
  isLocked: boolean;
}

/** The menu: the server sends it ($shell) and says which entries a lock disables. */
const entries = computed(() => (routeables.shell.value?.state['entries'] as MenuEntry[] | undefined) ?? []);
const theme = computed(() => String((routeables.shell.value?.state as State | undefined)?.['theme'] ?? 'Default'));
/** The menu marks the first segment of the address: /cats/Tom is still in Cats. */
const current = computed(() => '/' + (routeables.page.value?.route.split('/').filter(Boolean)[0] ?? ''));

// The theme the view models asked for (IRouteableRequestedThemeService). Default follows the browser.
watchEffect(() => document.documentElement.setAttribute('data-theme', theme.value.toLowerCase()));
void routeables.start();

// A link is a route: the server navigates and the page that arrives replaces this one. No reload.
function open(event: Event, route: string): void {
  event.preventDefault();
  void routeables.goTo(route);
}

const isActive = (entry: MenuEntry): boolean => entry.route.toLowerCase() === current.value.toLowerCase();
const cycleTheme = (): void => void routeables.run('$shell', routeables.shell.value?.state['themeCommand']);
</script>

<template>
  <header class="site-header">
    <button type="button" class="drawer-toggle" aria-controls="drawer" :aria-expanded="!routeables.drawerClosed.value"
            aria-label="Show or hide the menu" @click="routeables.toggleDrawer()">☰</button>
    <a class="brand" href="/cats" @click="open($event, '/cats')">Routeables Animals</a>
    <!-- The theme is asked of the view model like on the other platforms; Default follows the browser. -->
    <button type="button" class="drawer-toggle theme-form" :title="`Theme: ${theme} (click for the next one)`"
            :aria-label="`Change the theme, now ${theme}`" @click="cycleTheme()">◐</button>
  </header>

  <main role="main">
    <div class="shell" :class="{ 'drawer-closed': routeables.drawerClosed.value }">
      <!-- The side panel: one entry per entry of the main view model, as in the other samples. -->
      <nav class="drawer" id="drawer" aria-label="Menu">
        <template v-for="entry in entries" :key="entry.route">
          <!-- A page published a lock (Features): the entry cannot be followed until it publishes the unlock. -->
          <span v-if="entry.isLocked" class="drawer-link locked" title="Locked">
            <span class="drawer-icon" aria-hidden="true">{{ entry.glyph }}</span>
            <span class="drawer-name">{{ entry.name }}</span>
          </span>
          <a v-else class="drawer-link" :class="{ active: isActive(entry) }" :aria-current="isActive(entry) ? 'page' : undefined"
             :href="entry.route" @click="open($event, entry.route)">
            <span class="drawer-icon" aria-hidden="true">{{ entry.glyph }}</span>
            <span class="drawer-text">
              <span class="drawer-name">{{ entry.name }}</span>
              <span v-if="entry.description" class="drawer-description">{{ entry.description }}</span>
            </span>
          </a>
        </template>
      </nav>

      <div class="shell-content">
        <div style="padding: 20px">
          <p v-if="routeables.error.value" class="error" role="alert">{{ routeables.error.value }}</p>
          <PageView v-if="routeables.page.value" :page="routeables.page.value" />
          <p v-else-if="routeables.connected.value" class="empty">Loading…</p>
          <p v-else class="empty">Offline: the server is not reachable.</p>
        </div>
      </div>
    </div>
  </main>

  <Interactions />
</template>
