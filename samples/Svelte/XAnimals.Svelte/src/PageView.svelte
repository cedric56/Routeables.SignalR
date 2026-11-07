<script lang="ts">
  import type { Component } from 'svelte';
  import type { Page } from '@routeables/client';
  import { animalDetails, animalLists } from './routeables';
  import About from './pages/About.svelte';
  import Adopt from './pages/Adopt.svelte';
  import AnimalDetail from './pages/AnimalDetail.svelte';
  import AnimalList from './pages/AnimalList.svelte';
  import Dialogs from './pages/Dialogs.svelte';
  import Features from './pages/Features.svelte';
  import History from './pages/History.svelte';
  import LayoutBar from './pages/LayoutBar.svelte';
  import LayoutLogo from './pages/LayoutLogo.svelte';
  import LayoutMenu from './pages/LayoutMenu.svelte';
  import Members from './pages/Members.svelte';
  import Regions from './pages/Regions.svelte';
  import Tabs from './pages/Tabs.svelte';

  /**
   * Chooses how to draw a page: the view model decides which page it is, this picks the component for it. The same component draws
   * the page of the main router and the one a region shows, because a page does not know where it is shown.
   */
  let { page, target = '' }: { page: Page; target?: string } = $props();

  const views: Record<string, Component<{ page: Page; target: string }>> = {
    AboutViewModel: About,
    AdoptViewModel: Adopt,
    DialogsViewModel: Dialogs,
    FeaturesViewModel: Features,
    HistoryViewModel: History,
    MembersViewModel: Members,
    RegionsViewModel: Regions,
    LayoutBarViewModel: LayoutBar,
    LayoutMenuViewModel: LayoutMenu,
    LogoViewModel: LayoutLogo,
    TabsViewModel: Tabs,
  };

  const View = $derived(
    animalLists.includes(page.viewModel) ? AnimalList : animalDetails.includes(page.viewModel) ? AnimalDetail : views[page.viewModel],
  );
</script>

{#if View}
  <!-- Another page is another component, even of the same type: what it held (a search box) starts again. -->
  {#key page.sequence}
    <View {page} {target} />
  {/key}
{:else}
  <p class="no-results">No view for {page.viewModel}.</p>
{/if}
