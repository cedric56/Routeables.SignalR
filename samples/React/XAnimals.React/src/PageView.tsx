import type { ComponentType } from 'react';
import { animalDetails, animalLists } from './routeables';
import type { PageProps } from './hooks';
import { About } from './pages/About';
import { Adopt } from './pages/Adopt';
import { AnimalDetail } from './pages/AnimalDetail';
import { AnimalList } from './pages/AnimalList';
import { Dialogs } from './pages/Dialogs';
import { Essentials } from './pages/Essentials';
import { Features } from './pages/Features';
import { History } from './pages/History';
import { LayoutBar } from './pages/LayoutBar';
import { LayoutLogo } from './pages/LayoutLogo';
import { LayoutMenu } from './pages/LayoutMenu';
import { Members } from './pages/Members';
import { Regions } from './pages/Regions';
import { Tabs } from './pages/Tabs';

const views: Record<string, ComponentType<PageProps>> = {
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

/**
 * Chooses how to draw a page: the view model decides which page it is, this picks the component for it. The same component draws
 * the page of the main router and the one a region shows, because a page does not know where it is shown.
 */
export function PageView({ page, target }: PageProps) {
  const viewModel = page.viewModel;
  const View = animalLists.includes(viewModel) ? AnimalList : animalDetails.includes(viewModel) ? AnimalDetail : views[viewModel];
  // Another page is another component, even of the same type (the key): what it held (a search box) starts again.
  return View ? <View key={page.sequence} page={page} target={target} /> : <p className="no-results">No view for {viewModel}.</p>;
}
