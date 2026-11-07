import { For, Show } from 'solid-js';
import { firstSegment, routeables } from '../routeables';
import type { State } from '../routeables';
import type { PageProps } from '../store';

interface Animal { name: string; location: string; details: string; thumbnailUrl: string; photoUrl: string }

/** The list of a species. A list that a region shows (hostsDetail is false) does not leave the page: no search, no link to a detail. */
export function AnimalList(props: PageProps) {
  const s = (): State => props.page.state;
  // The first segment of the address of a page: /cats/Abyssinian is the page of the cats.
  const kind = (): string => firstSegment(props.page.route);
  const title = (): string => kind().charAt(0).toUpperCase() + kind().slice(1);
  const animals = (): Animal[] => s().filteredAnimals ?? [];
  const detail = (animal: Animal): string => `/${kind()}/${encodeURIComponent(animal.name)}`;

  const item = (animal: Animal) => (
    <>
      <img src={animal.thumbnailUrl} alt={animal.name} width={56} height={68} loading="lazy" />
      <span class="animal-text">
        <span class="animal-name">{animal.name}</span>
        <span class="animal-location">{animal.location}</span>
      </span>
    </>
  );

  return (
    <div class="animals-page">
      <h1>{title()}</h1>
      <Show when={s().hostsDetail}>
        <div class="search-row" style={{ display: 'flex', gap: '0.5rem', 'align-items': 'center' }}>
          {/* The box keeps what is typed (set once): the server answers each key after the next one may already be typed. */}
          <input class="search-box" style={{ flex: 1 }} type="search" placeholder="Enter search term" aria-label="Search animals"
                 ref={el => (el.value = s().searchText ?? '')} onInput={e => void routeables.set(props.target, 'searchText', e.currentTarget.value)} />
        </div>
      </Show>
      <Show when={!animals().length}><p class="no-results">No {kind()} found.</p></Show>
      <div class="animal-list">
        <For each={animals()}>
          {animal => (
            <Show when={s().hostsDetail} fallback={<div class="animal-item">{item(animal)}</div>}>
              {/* A link is a route: the server navigates and the page that arrives replaces this one. No reload. */}
              <a class="animal-item" href={detail(animal)} onClick={e => { e.preventDefault(); void routeables.goTo(detail(animal)); }}>{item(animal)}</a>
            </Show>
          )}
        </For>
      </div>
    </div>
  );
}
