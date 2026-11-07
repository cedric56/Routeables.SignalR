import { createMemo, Show } from 'solid-js';
import type { Component } from 'solid-js';
import { Dynamic } from 'solid-js/web';
import { animalDetails, animalLists } from './routeables';
import type { PageProps } from './store';
import { About } from './pages/About';
import { Adopt } from './pages/Adopt';
import { AnimalDetail } from './pages/AnimalDetail';
import { AnimalList } from './pages/AnimalList';
import { Dialogs } from './pages/Dialogs';
import { Features } from './pages/Features';
import { History } from './pages/History';
import { LayoutBar } from './pages/LayoutBar';
import { LayoutLogo } from './pages/LayoutLogo';
import { LayoutMenu } from './pages/LayoutMenu';
import { Members } from './pages/Members';
import { Regions } from './pages/Regions';
import { Tabs } from './pages/Tabs';

const views: Record<string, Component<PageProps>> = {
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

/**
 * Chooses how to draw a page: the view model decides which page it is, this picks the component for it. The same component draws
 * the page of the main router and the one a region shows, because a page does not know where it is shown.
 */
export function PageView(props: PageProps) {
  const view = createMemo(() => {
    const viewModel = props.page.viewModel;
    return animalLists.includes(viewModel) ? AnimalList : animalDetails.includes(viewModel) ? AnimalDetail : views[viewModel];
  });
  const sequence = createMemo(() => props.page.sequence);

  return (
    <Show when={view()} fallback={<p class="no-results">No view for {props.page.viewModel}.</p>}>
      {/* Another page is another component, even of the same type (keyed by its number): what it held (a search box) starts again. */}
      <Show when={sequence()} keyed>
        {_sequence => <Dynamic component={view()} page={props.page} target={props.target} />}
      </Show>
    </Show>
  );
}
