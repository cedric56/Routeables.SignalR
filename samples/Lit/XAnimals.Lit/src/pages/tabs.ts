import { html } from 'lit';
import { customElement, state } from 'lit/decorators.js';
import { routeables, tabs } from '../routeables';
import { LightElement } from '../base';
import '../region';

/** The items of a tab control are routes: each tab shows its route in the region of the page, and the region draws it. */
@customElement('xa-tabs')
export class Tabs extends LightElement {
  @state() private selected = '';

  // A tab control selects its first item when it appears.
  override connectedCallback(): void {
    super.connectedCallback();
    this.select(tabs[0]!.route);
  }

  private select(route: string): void {
    this.selected = route;
    void routeables.goToIn('tabs-demo', route);
  }

  protected override render() {
    return html`
      <div class="about-page">
        <div class="actions" role="tablist">
          ${tabs.map(tab => html`
            <button type="button" role="tab" class="adopt-choice" aria-selected=${String(tab.route === this.selected)}
                    @click=${() => this.select(tab.route)}>${tab.header}</button>`)}
        </div>
      </div>
      <div class="about-page">
        <section class="about-group"><xa-region name="tabs-demo"></xa-region></section>
      </div>`;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'xa-tabs': Tabs;
  }
}
