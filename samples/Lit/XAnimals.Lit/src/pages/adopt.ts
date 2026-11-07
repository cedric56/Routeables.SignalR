import { html } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import { repeat } from 'lit/directives/repeat.js';
import type { Page } from '@routeables/client';
import { routeables } from '../routeables';
import type { State } from '../routeables';
import { LightElement, TargetController } from '../base';

interface Pet { animal: { photoUrl: string }; name: string; location: string; glyph: string }
interface Choice { name: string; title: string; adoptCommand: unknown }

/** One button per animal: adopting it routes its card into the region "adopt". An element: it follows `$pets`. */
@customElement('xa-adopt')
export class Adopt extends LightElement {
  @property({ attribute: false }) page!: Page;
  @property() target = '';
  // The cards are the history of the region of the adopted animals (`$pets`).
  private readonly pets = new TargetController(this, () => '$pets');

  protected override render() {
    const s = this.page.state as State;
    const choices = (s.choices as Choice[] | undefined) ?? [];
    const pets = (this.pets.value?.state['pets'] as Pet[] | undefined) ?? [];
    return html`
      <div class="about-page">
        <section class="about-group adopt-choices">
          ${repeat(choices, choice => choice.name, choice => html`
            <button type="button" class="adopt-choice" @click=${() => routeables.run(this.target, choice.adoptCommand)}>${choice.title}</button>`)}
        </section>

        <section class="about-group adopt-bar">
          <p class="nav-status" role="status">${s.status}</p>
          <button type="button" class="adopt-choice" @click=${() => routeables.run(this.target, s.releaseCommand)}>Release the last</button>
          <button type="button" class="adopt-choice" @click=${() => routeables.run(this.target, s.releaseAllCommand)}>Release all</button>
        </section>

        <div class="adopt-zone" style="display: flex; flex-wrap: wrap; gap: 0.8rem">
          ${repeat(pets, pet => pet.name, pet => html`
            <article class="pet-card">
              <img src=${pet.animal.photoUrl} alt=${pet.name} width="150" height="190" />
              <strong>${pet.name}</strong>
              <span class="pet-location">${pet.location}</span>
              <span class="pet-glyph">${pet.glyph}</span>
            </article>`)}
        </div>
      </div>`;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'xa-adopt': Adopt;
  }
}
