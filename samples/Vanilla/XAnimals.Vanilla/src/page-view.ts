import type { Page } from '@routeables/client';
import { animalDetails, animalLists } from './routeables';
import { h } from './dom';
import type { View } from './dom';
import { about } from './pages/about';
import { adopt } from './pages/adopt';
import { animalDetail } from './pages/animal-detail';
import { animalList } from './pages/animal-list';
import { dialogs } from './pages/dialogs';
import { features } from './pages/features';
import { history } from './pages/history';
import { layoutBar, layoutLogo, layoutMenu } from './pages/layout';
import { members } from './pages/members';
import { regions } from './pages/regions';
import { tabs } from './pages/tabs';

/** How a page is drawn: its page and the target that shows it give a view. */
export type ViewFactory = (page: Page, target: string) => View;

const views: Record<string, ViewFactory> = {
  AboutViewModel: about,
  AdoptViewModel: adopt,
  DialogsViewModel: dialogs,
  FeaturesViewModel: features,
  HistoryViewModel: history,
  MembersViewModel: members,
  RegionsViewModel: regions,
  LayoutBarViewModel: layoutBar,
  LayoutMenuViewModel: layoutMenu,
  LogoViewModel: layoutLogo,
  TabsViewModel: tabs,
};

/**
 * Chooses how to draw a page: the view model decides which page it is, this picks the view for it. The same view draws the page of the
 * main router and the one a region shows, because a page does not know where it is shown.
 */
export function pageView(page: Page, target: string): View {
  const viewModel = page.viewModel;
  const factory = animalLists.includes(viewModel) ? animalList : animalDetails.includes(viewModel) ? animalDetail : views[viewModel];
  if (factory) return factory(page, target);
  const node = h('p', { class: 'no-results' }, `No view for ${viewModel}.`);
  return { node, update: () => {}, destroy: () => {} };
}

/**
 * Shows the pages that follow one another in a zone: the state of the same page (the same number) updates its view; another page (even of
 * the same type) replaces it, so that what it held (a search box) starts again.
 */
export class PageSlot {
  private view: View | null = null;
  private sequence = 0;

  constructor(private readonly zone: Element, private readonly target: string, private readonly wrap: (node: Node) => Node = node => node) {}

  show(page: Page | null): void {
    if (page && this.view && page.sequence === this.sequence) {
      this.view.update(page);
      return;
    }
    this.view?.destroy();
    this.view = page ? pageView(page, this.target) : null;
    this.sequence = page?.sequence ?? 0;
    this.zone.replaceChildren(...(this.view ? [this.wrap(this.view.node)] : []));
  }

  destroy(): void {
    this.view?.destroy();
    this.view = null;
  }
}
