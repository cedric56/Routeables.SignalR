import { html, nothing } from 'lit';
import { repeat } from 'lit/directives/repeat.js';
import type { Page } from '@routeables/client';
import { firstSegment, routeables } from '../routeables';
import type { State } from '../routeables';

interface Animal { name: string; location: string; details: string; thumbnailUrl: string; photoUrl: string }

/** The list of a species. A list that a region shows (hostsDetail is false) does not leave the page: no search, no link to a detail. */
export const animalList = (page: Page, target: string) => {
  const s = page.state as State;
  // The first segment of the address of a page: /cats/Abyssinian is the page of the cats.
  const kind = firstSegment(page.route);
  const title = kind.charAt(0).toUpperCase() + kind.slice(1);
  const animals = (s.filteredAnimals as Animal[] | undefined) ?? [];
  const detail = (animal: Animal): string => `/${kind}/${encodeURIComponent(animal.name)}`;
  const item = (animal: Animal) => html`
    <img src=${animal.thumbnailUrl} alt=${animal.name} width="56" height="68" loading="lazy" />
    <span class="animal-text">
      <span class="animal-name">${animal.name}</span>
      <span class="animal-location">${animal.location}</span>
    </span>`;

  return html`
    <div class="animals-page">
      <h1>${title}</h1>
      ${s.hostsDetail ? html`
        <div class="search-row" style="display: flex; gap: 0.5rem; align-items: center">
          <!-- The box keeps what is typed (defaultValue): the server answers each key after the next one may already be typed. -->
          <input class="search-box" style="flex: 1" type="search" placeholder="Enter search term" aria-label="Search animals"
                 .defaultValue=${s.searchText ?? ''} @input=${(e: Event) => routeables.set(target, 'searchText', (e.target as HTMLInputElement).value)} />
        </div>` : nothing}
      ${animals.length ? nothing : html`<p class="no-results">No ${kind} found.</p>`}
      <div class="animal-list">
        ${repeat(animals, animal => animal.name, animal => s.hostsDetail
          // A link is a route: the server navigates and the page that arrives replaces this one. No reload.
          ? html`<a class="animal-item" href=${detail(animal)} @click=${(e: Event) => { e.preventDefault(); void routeables.goTo(detail(animal)); }}>${item(animal)}</a>`
          : html`<div class="animal-item">${item(animal)}</div>`)}
      </div>
    </div>`;
};
