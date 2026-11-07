import { html } from 'lit';
import type { TemplateResult } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import { keyed } from 'lit/directives/keyed.js';
import type { Page } from '@routeables/client';
import { animalDetails, animalLists } from './routeables';
import { LightElement } from './base';
import { about } from './pages/about';
import './pages/adopt';
import { animalDetail } from './pages/animal-detail';
import { animalList } from './pages/animal-list';
import { dialogs } from './pages/dialogs';
import { features } from './pages/features';
import { history } from './pages/history';
import { layoutBar, layoutLogo, layoutMenu } from './pages/layout';
import { members } from './pages/members';
import { regions } from './pages/regions';
import './pages/tabs';

/** How a page is drawn: its page and the target that shows it give a template. */
export type View = (page: Page, target: string) => TemplateResult;

const views: Record<string, View> = {
  AboutViewModel: about,
  AdoptViewModel: (page, target) => html`<xa-adopt .page=${page} .target=${target}></xa-adopt>`,
  DialogsViewModel: dialogs,
  FeaturesViewModel: features,
  HistoryViewModel: history,
  MembersViewModel: members,
  RegionsViewModel: regions,
  LayoutBarViewModel: layoutBar,
  LayoutMenuViewModel: layoutMenu,
  LogoViewModel: layoutLogo,
  TabsViewModel: () => html`<xa-tabs></xa-tabs>`,
};

/**
 * Chooses how to draw a page: the view model decides which page it is, this picks the template for it. The same template draws
 * the page of the main router and the one a region shows, because a page does not know where it is shown.
 */
@customElement('xa-page-view')
export class PageView extends LightElement {
  @property({ attribute: false }) page!: Page;
  @property() target = '';

  protected override render() {
    const viewModel = this.page.viewModel;
    const view = animalLists.includes(viewModel) ? animalList : animalDetails.includes(viewModel) ? animalDetail : views[viewModel];
    // Another page is another template, even of the same type (keyed by its number): what it held (a search box) starts again.
    return view ? keyed(this.page.sequence, view(this.page, this.target)) : html`<p class="no-results">No view for ${viewModel}.</p>`;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'xa-page-view': PageView;
  }
}
