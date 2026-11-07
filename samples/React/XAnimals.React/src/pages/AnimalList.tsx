import { firstSegment, routeables } from '../routeables';
import type { State } from '../routeables';
import type { PageProps } from '../hooks';

interface Animal { name: string; location: string; details: string; thumbnailUrl: string; photoUrl: string }

/** The list of a species. A list that a region shows (hostsDetail is false) does not leave the page: no search, no link to a detail. */
export function AnimalList({ page, target }: PageProps) {
  const s = page.state as State;
  // The first segment of the address of a page: /cats/Abyssinian is the page of the cats.
  const kind = firstSegment(page.route);
  const title = kind.charAt(0).toUpperCase() + kind.slice(1);
  const animals = (s.filteredAnimals as Animal[] | undefined) ?? [];
  const detail = (animal: Animal): string => `/${kind}/${encodeURIComponent(animal.name)}`;

  const item = (animal: Animal) => (
    <>
      <img src={animal.thumbnailUrl} alt={animal.name} width={56} height={68} loading="lazy" />
      <span className="animal-text">
        <span className="animal-name">{animal.name}</span>
        <span className="animal-location">{animal.location}</span>
      </span>
    </>
  );

  return (
    <div className="animals-page">
      <h1>{title}</h1>
      {s.hostsDetail && (
        <div className="search-row" style={{ display: 'flex', gap: '0.5rem', alignItems: 'center' }}>
          {/* The box keeps what is typed (defaultValue): the server answers each key after the next one may already be typed. */}
          <input key={page.sequence} className="search-box" style={{ flex: 1 }} type="search" placeholder="Enter search term" aria-label="Search animals"
                 defaultValue={s.searchText ?? ''} onChange={e => void routeables.set(target, 'searchText', e.target.value)} />
        </div>
      )}
      {!animals.length && <p className="no-results">No {kind} found.</p>}
      <div className="animal-list">
        {animals.map(animal => s.hostsDetail ? (
          // A link is a route: the server navigates and the page that arrives replaces this one. No reload.
          <a key={animal.name} className="animal-item" href={detail(animal)}
             onClick={e => { e.preventDefault(); void routeables.goTo(detail(animal)); }}>{item(animal)}</a>
        ) : (
          <div key={animal.name} className="animal-item">{item(animal)}</div>
        ))}
      </div>
    </div>
  );
}
