<script setup lang="ts">
import { computed } from 'vue';
import type { Page } from '@routeables/client';
import { routeables } from '../routeables';
import type { State } from '../routeables';

const props = withDefaults(defineProps<{ page: Page; target?: string }>(), { target: '' });
const s = computed(() => props.page.state as State);

interface Animal { name: string; location: string; details: string; thumbnailUrl: string; photoUrl: string }

// The first segment of the address of a page: /cats/Abyssinian is the page of the cats.
const kind = computed(() => props.page.route.split('/').filter(Boolean)[0] ?? '');
const title = computed(() => kind.value.charAt(0).toUpperCase() + kind.value.slice(1));
const animals = computed(() => (s.value['filteredAnimals'] as Animal[] | undefined) ?? []);

const detail = (animal: Animal): string => `/${kind.value}/${encodeURIComponent(animal.name)}`;

// A link is a route: the server navigates and the page that arrives replaces this one. No reload.
function open(event: Event, animal: Animal): void {
  event.preventDefault();
  void routeables.goTo(detail(animal));
}

const search = (event: Event): void => void routeables.set(props.target, 'searchText', (event.target as HTMLInputElement).value);
</script>

<!-- The list of a species. A list that a region shows (hostsDetail is false) does not leave the page: no search, no link to a detail. -->
<template>
  <div class="animals-page">
    <h1>{{ title }}</h1>
    <div v-if="s.hostsDetail" class="search-row">
      <input class="search-box" type="search" placeholder="Enter search term" aria-label="Search animals"
             :value="s.searchText" @input="search" />
    </div>
    <p v-if="!animals.length" class="no-results">No {{ kind }} found.</p>
    <div class="animal-list">
      <template v-for="animal in animals" :key="animal.name">
        <a v-if="s.hostsDetail" class="animal-item" :href="detail(animal)" @click="open($event, animal)">
          <img :src="animal.thumbnailUrl" :alt="animal.name" width="56" height="68" loading="lazy" />
          <span class="animal-text">
            <span class="animal-name">{{ animal.name }}</span>
            <span class="animal-location">{{ animal.location }}</span>
          </span>
        </a>
        <div v-else class="animal-item">
          <img :src="animal.thumbnailUrl" :alt="animal.name" width="56" height="68" loading="lazy" />
          <span class="animal-text">
            <span class="animal-name">{{ animal.name }}</span>
            <span class="animal-location">{{ animal.location }}</span>
          </span>
        </div>
      </template>
    </div>
  </div>
</template>

<style scoped>
.search-row { display: flex; gap: 0.5rem; align-items: center; }
.search-row .search-box { flex: 1; }
</style>
