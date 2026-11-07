import { html } from 'lit';
import type { Page } from '@routeables/client';
import { firstSegment, routeables } from '../routeables';
import type { State } from '../routeables';

interface Animal { name: string; location: string; details: string; photoUrl: string }

export const animalDetail = (page: Page) => {
  const animal = (page.state as State).animal as Animal | null | undefined;
  const list = '/' + firstSegment(page.route);
  return html`
    <div class="monkey-detail">
      ${animal ? html`
        <h1 class="title">${animal.name}</h1>
        <p class="location">${animal.location}</p>
        <img src=${animal.photoUrl} alt=${animal.name} width="260" height="330" />
        <p class="body-text">${animal.details}</p>` : html`
        <h1 class="title">Animal not found</h1>
        <p class="body-text">This animal was not found.</p>`}
      <a class="back-btn" href=${list} @click=${(e: Event) => { e.preventDefault(); void routeables.goTo(list); }}>Back to list</a>
    </div>`;
};
