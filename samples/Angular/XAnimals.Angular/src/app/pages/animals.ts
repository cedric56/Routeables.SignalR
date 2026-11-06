import { Component, computed, inject, input } from '@angular/core';
import type { Page } from '@routeables/client';
import { Routeables } from '../routeables';
import type { State } from '../routeables';

interface Animal {
  name: string;
  location: string;
  details: string;
  thumbnailUrl: string;
  photoUrl: string;
}

/** The first segment of the address of a page: /cats/Abyssinian is the page of the cats. */
function species(page: Page): string {
  return page.route.split('/').filter(Boolean)[0] ?? '';
}

/** The list of a species. A list that a region shows (hostsDetail is false) does not leave the page: no search, no link to a detail. */
@Component({
  selector: 'app-animal-list',
  template: `
    <div class="animals-page">
      <h1>{{ title() }}</h1>
      @if (s().hostsDetail) {
        <div class="search-row">
          <input class="search-box" type="search" placeholder="Enter search term" aria-label="Search animals"
                 [value]="s().searchText" (input)="search($event)" />
        </div>
      }
      @if (!animals().length) { <p class="no-results">No {{ kind() }} found.</p> }
      <div class="animal-list">
        @for (animal of animals(); track animal.name) {
          @if (s().hostsDetail) {
            <a class="animal-item" [href]="detail(animal)" (click)="open($event, animal)">
              <img [src]="animal.thumbnailUrl" [alt]="animal.name" width="56" height="68" loading="lazy" />
              <span class="animal-text">
                <span class="animal-name">{{ animal.name }}</span>
                <span class="animal-location">{{ animal.location }}</span>
              </span>
            </a>
          } @else {
            <div class="animal-item">
              <img [src]="animal.thumbnailUrl" [alt]="animal.name" width="56" height="68" loading="lazy" />
              <span class="animal-text">
                <span class="animal-name">{{ animal.name }}</span>
                <span class="animal-location">{{ animal.location }}</span>
              </span>
            </div>
          }
        }
      </div>
    </div>
  `,
  styles: `.search-row { display: flex; gap: .5rem; align-items: center; } .search-row .search-box { flex: 1; }`,
})
export class AnimalList {
  readonly page = input.required<Page>();
  readonly target = input('');
  private readonly routeables = inject(Routeables);
  protected readonly s = computed(() => this.page().state as State);
  protected readonly kind = computed(() => species(this.page()));
  protected readonly title = computed(() => this.kind().charAt(0).toUpperCase() + this.kind().slice(1));
  protected readonly animals = computed(() => (this.s()['filteredAnimals'] as Animal[] | undefined) ?? []);

  protected detail(animal: Animal): string {
    return `/${this.kind()}/${encodeURIComponent(animal.name)}`;
  }

  protected open(event: Event, animal: Animal): void {
    // A link is a route: the server navigates and the page that arrives replaces this one. No reload.
    event.preventDefault();
    void this.routeables.goTo(this.detail(animal));
  }

  protected search(event: Event): void {
    void this.routeables.set(this.target(), 'searchText', (event.target as HTMLInputElement).value);
  }
}

@Component({
  selector: 'app-animal-detail',
  template: `
    <div class="monkey-detail">
      @if (animal(); as a) {
        <h1 class="title">{{ a.name }}</h1>
        <p class="location">{{ a.location }}</p>
        <img [src]="a.photoUrl" [alt]="a.name" width="260" height="330" />
        <p class="body-text">{{ a.details }}</p>
      } @else {
        <h1 class="title">Animal not found</h1>
        <p class="body-text">This animal was not found.</p>
      }
      <a class="back-btn" [href]="list()" (click)="back($event)">Back to list</a>
    </div>
  `,
})
export class AnimalDetail {
  readonly page = input.required<Page>();
  private readonly routeables = inject(Routeables);
  protected readonly animal = computed(() => (this.page().state as State)['animal'] as Animal | null | undefined);
  protected readonly list = computed(() => '/' + species(this.page()));

  protected back(event: Event): void {
    event.preventDefault();
    void this.routeables.goTo(this.list());
  }
}
