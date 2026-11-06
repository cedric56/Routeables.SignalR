<script setup lang="ts">
import { computed } from 'vue';
import type { Page } from '@routeables/client';
import { routeables } from '../routeables';
import type { State } from '../routeables';

const props = withDefaults(defineProps<{ page: Page; target?: string }>(), { target: '' });
const s = computed(() => props.page.state as State);

interface Animal { name: string; location: string; details: string; photoUrl: string }

const animal = computed(() => s.value['animal'] as Animal | null | undefined);
const list = computed(() => '/' + (props.page.route.split('/').filter(Boolean)[0] ?? ''));

function back(event: Event): void {
  event.preventDefault();
  void routeables.goTo(list.value);
}
</script>

<template>
  <div class="monkey-detail">
    <template v-if="animal">
      <h1 class="title">{{ animal.name }}</h1>
      <p class="location">{{ animal.location }}</p>
      <img :src="animal.photoUrl" :alt="animal.name" width="260" height="330" />
      <p class="body-text">{{ animal.details }}</p>
    </template>
    <template v-else>
      <h1 class="title">Animal not found</h1>
      <p class="body-text">This animal was not found.</p>
    </template>
    <a class="back-btn" :href="list" @click="back">Back to list</a>
  </div>
</template>
