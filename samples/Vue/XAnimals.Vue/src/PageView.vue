<script setup lang="ts">
import { computed } from 'vue';
import type { Component } from 'vue';
import type { Page } from '@routeables/client';
import About from './pages/About.vue';
import Adopt from './pages/Adopt.vue';
import AnimalDetail from './pages/AnimalDetail.vue';
import AnimalList from './pages/AnimalList.vue';
import Dialogs from './pages/Dialogs.vue';
import Essentials from './pages/Essentials.vue';
import Features from './pages/Features.vue';
import History from './pages/History.vue';
import LayoutBar from './pages/LayoutBar.vue';
import LayoutLogo from './pages/LayoutLogo.vue';
import LayoutMenu from './pages/LayoutMenu.vue';
import Members from './pages/Members.vue';
import Regions from './pages/Regions.vue';
import Tabs from './pages/Tabs.vue';

/**
 * Chooses how to draw a page: the view model decides which page it is, this picks the component for it. The same component draws
 * the page of the main router and the one a region shows, because a page does not know where it is shown.
 */
const props = withDefaults(defineProps<{ page: Page; target?: string }>(), { target: '' });

const lists = ['Cats', 'Dogs', 'Monkeys', 'Elephants', 'Bears', 'Pandas'].map(name => `${name}ViewModel`);
const details = [...['Cat', 'Dog', 'Monkey', 'Elephant', 'Bear'].map(name => `${name}DetailViewModel`), 'PandaDetail'];

const views: Record<string, Component> = {
  AboutViewModel: About,
  AdoptViewModel: Adopt,
  DialogsViewModel: Dialogs,
  EssentialsViewModel: Essentials,
  FeaturesViewModel: Features,
  HistoryViewModel: History,
  MembersViewModel: Members,
  RegionsViewModel: Regions,
  LayoutBarViewModel: LayoutBar,
  LayoutMenuViewModel: LayoutMenu,
  LogoViewModel: LayoutLogo,
  TabsViewModel: Tabs,
};

const view = computed<Component | undefined>(() => {
  const viewModel = props.page.viewModel;
  return lists.includes(viewModel) ? AnimalList : details.includes(viewModel) ? AnimalDetail : views[viewModel];
});
</script>

<template>
  <component :is="view" v-if="view" :page="page" :target="target" />
  <p v-else class="no-results">No view for {{ page.viewModel }}.</p>
</template>
