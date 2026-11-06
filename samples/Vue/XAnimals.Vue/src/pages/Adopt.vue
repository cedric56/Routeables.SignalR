<script setup lang="ts">
import { computed } from 'vue';
import type { Page } from '@routeables/client';
import { routeables } from '../routeables';
import type { State } from '../routeables';

const props = withDefaults(defineProps<{ page: Page; target?: string }>(), { target: '' });
const s = computed(() => props.page.state as State);
interface Pet { animal: { photoUrl: string }; name: string; location: string; glyph: string }
interface Choice { name: string; title: string; adoptCommand: unknown }

// The cards are the history of the region of the adopted animals (`$pets`).
const petsPage = routeables.follow('$pets');
const choices = computed(() => (s.value['choices'] as Choice[] | undefined) ?? []);
const pets = computed(() => (petsPage.value?.state['pets'] as Pet[] | undefined) ?? []);
</script>

<!-- One button per animal: adopting it routes its card into the region "adopt". -->
<template>
  <div class="about-page">
    <section class="about-group adopt-choices">
      <button v-for="choice in choices" :key="choice.name" type="button" class="adopt-choice"
              @click="routeables.run(target, choice.adoptCommand)">{{ choice.title }}</button>
    </section>

    <section class="about-group adopt-bar">
      <p class="nav-status" role="status">{{ s.status }}</p>
      <button type="button" class="adopt-choice" @click="routeables.run(target, s.releaseCommand)">Release the last</button>
      <button type="button" class="adopt-choice" @click="routeables.run(target, s.releaseAllCommand)">Release all</button>
    </section>

    <div class="adopt-zone" style="display: flex; flex-wrap: wrap; gap: 0.8rem">
      <article v-for="pet in pets" :key="pet.name" class="pet-card">
        <img :src="pet.animal.photoUrl" :alt="pet.name" width="150" height="190" />
        <strong>{{ pet.name }}</strong>
        <span class="pet-location">{{ pet.location }}</span>
        <span class="pet-glyph">{{ pet.glyph }}</span>
      </article>
    </div>
  </div>
</template>
