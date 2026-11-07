import { html, nothing } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import { keyed } from 'lit/directives/keyed.js';
import { LightElement, TargetController } from './base';
import './page-view';

/**
 * The zone of a region: it shows what the router of the region displays. Nothing assigns a page to it; the page is chosen by its
 * view model. A page that arrives plays the transition of the zone again (keyed by the number of the page).
 */
@customElement('xa-region')
export class Region extends LightElement {
  @property() name = '';
  @property() transition = 'None';
  private readonly page = new TargetController(this, () => this.name);

  protected override render() {
    const page = this.page.value;
    if (!page) return nothing;
    return keyed(page.sequence, html`
      <div class="rt-enter rt-${this.transition.toLowerCase()}">
        <xa-page-view .page=${page} target=${this.name}></xa-page-view>
      </div>`);
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'xa-region': Region;
  }
}
