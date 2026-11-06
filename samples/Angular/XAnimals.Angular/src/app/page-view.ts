import { Component, computed, input } from '@angular/core';
import type { Page } from '@routeables/client';
import { AboutPage } from './pages/about';
import { AdoptPage, PetCard } from './pages/adopt';
import { AnimalDetail, AnimalList } from './pages/animals';
import { DialogsPage } from './pages/dialogs';
import { FeaturesPage } from './pages/features';
import { HistoryPage } from './pages/history';
import { LayoutBar, LayoutLogo, LayoutMenu, RegionsPage } from './pages/regions';
import { MembersPage } from './pages/members';
import { TabsPage } from './pages/tabs';

const lists = ['Cats', 'Dogs', 'Monkeys', 'Elephants', 'Bears', 'Pandas'].map(name => `${name}ViewModel`);
const details = [...['Cat', 'Dog', 'Monkey', 'Elephant', 'Bear'].map(name => `${name}DetailViewModel`), 'PandaDetail'];

/**
 * Chooses how to draw a page: the view model decides which page it is, this picks the component for it. The same component draws
 * the page of the main router and the one a region shows, because a page does not know where it is shown.
 */
@Component({
  selector: 'app-page',
  imports: [
    AboutPage, AdoptPage, PetCard, AnimalList, AnimalDetail, DialogsPage, FeaturesPage, HistoryPage, MembersPage,
    RegionsPage, LayoutBar, LayoutMenu, LayoutLogo, TabsPage,
  ],
  template: `
    @switch (kind()) {
      @case ('list') { <app-animal-list [page]="page()" [target]="target()" /> }
      @case ('detail') { <app-animal-detail [page]="page()" /> }
      @case ('AboutViewModel') { <app-about [page]="page()" [target]="target()" /> }
      @case ('AdoptViewModel') { <app-adopt [page]="page()" [target]="target()" /> }
      @case ('PetViewModel') { <app-pet [page]="page()" /> }
      @case ('DialogsViewModel') { <app-dialogs [page]="page()" [target]="target()" /> }
      @case ('FeaturesViewModel') { <app-features [page]="page()" [target]="target()" /> }
      @case ('HistoryViewModel') { <app-history [page]="page()" [target]="target()" /> }
      @case ('MembersViewModel') { <app-members [page]="page()" [target]="target()" /> }
      @case ('RegionsViewModel') { <app-regions [page]="page()" [target]="target()" /> }
      @case ('LayoutBarViewModel') { <app-layout-bar [page]="page()" /> }
      @case ('LayoutMenuViewModel') { <app-layout-menu [page]="page()" [target]="target()" /> }
      @case ('LogoViewModel') { <app-layout-logo [page]="page()" /> }
      @case ('TabsViewModel') { <app-tabs [page]="page()" [target]="target()" /> }
      @default { <p class="no-results">No view for {{ page().viewModel }}.</p> }
    }
  `,
})
export class PageView {
  readonly page = input.required<Page>();
  /** What shows the page: "" for the main router, or the name of the region. */
  readonly target = input('');

  protected readonly kind = computed(() => {
    const viewModel = this.page().viewModel;
    return lists.includes(viewModel) ? 'list' : details.includes(viewModel) ? 'detail' : viewModel;
  });
}
