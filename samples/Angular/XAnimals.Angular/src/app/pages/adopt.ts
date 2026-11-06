import { Component, computed, inject, input } from '@angular/core';
import type { Page } from '@routeables/client';
import { Routeables } from '../routeables';
import type { State } from '../routeables';

interface Pet {
  animal: { photoUrl: string };
  name: string;
  location: string;
  glyph: string;
}

/** The card of one adopted animal. */
@Component({
  selector: 'app-pet',
  template: `
    <article class="pet-card">
      <img [src]="s().animal.photoUrl" [alt]="s().name" width="150" height="190" />
      <strong>{{ s().name }}</strong>
      <span class="pet-location">{{ s().location }}</span>
      <span class="pet-glyph">{{ s().glyph }}</span>
    </article>
  `,
})
export class PetCard {
  readonly page = input.required<Page>();
  protected readonly s = computed(() => this.page().state as State);
}

/** One button per animal: adopting it routes its card into the region "adopt". The cards are the history of that region (`$pets`). */
@Component({
  selector: 'app-adopt',
  template: `
    <div class="about-page">
      <section class="about-group adopt-choices">
        @for (choice of choices(); track choice.name) {
          <button type="button" class="adopt-choice" (click)="routeables.run(target(), choice.adoptCommand)">{{ choice.title }}</button>
        }
      </section>

      <section class="about-group adopt-bar">
        <p class="nav-status" role="status">{{ s().status }}</p>
        <button type="button" class="adopt-choice" (click)="routeables.run(target(), s().releaseCommand)">Release the last</button>
        <button type="button" class="adopt-choice" (click)="routeables.run(target(), s().releaseAllCommand)">Release all</button>
      </section>

      <div class="adopt-zone" style="display:flex;flex-wrap:wrap;gap:.8rem">
        @for (pet of pets(); track pet.name) {
          <article class="pet-card">
            <img [src]="pet.animal.photoUrl" [alt]="pet.name" width="150" height="190" />
            <strong>{{ pet.name }}</strong>
            <span class="pet-location">{{ pet.location }}</span>
            <span class="pet-glyph">{{ pet.glyph }}</span>
          </article>
        }
      </div>
    </div>
  `,
})
export class AdoptPage {
  readonly page = input.required<Page>();
  readonly target = input('');
  protected readonly routeables = inject(Routeables);
  private readonly petsPage = this.routeables.follow('$pets');
  protected readonly s = computed(() => this.page().state as State);
  protected readonly choices = computed(() => (this.s()['choices'] as { name: string; title: string; adoptCommand: unknown }[] | undefined) ?? []);
  protected readonly pets = computed(() => (this.petsPage()?.state['pets'] as Pet[] | undefined) ?? []);
}
