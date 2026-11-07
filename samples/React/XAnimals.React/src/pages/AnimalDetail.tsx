import { firstSegment, routeables } from '../routeables';
import type { State } from '../routeables';
import type { PageProps } from '../hooks';

interface Animal { name: string; location: string; details: string; photoUrl: string }

export function AnimalDetail({ page }: PageProps) {
  const s = page.state as State;
  const animal = s.animal as Animal | null | undefined;
  const list = '/' + firstSegment(page.route);

  return (
    <div className="monkey-detail">
      {animal ? (
        <>
          <h1 className="title">{animal.name}</h1>
          <p className="location">{animal.location}</p>
          <img src={animal.photoUrl} alt={animal.name} width={260} height={330} />
          <p className="body-text">{animal.details}</p>
        </>
      ) : (
        <>
          <h1 className="title">Animal not found</h1>
          <p className="body-text">This animal was not found.</p>
        </>
      )}
      <a className="back-btn" href={list} onClick={e => { e.preventDefault(); void routeables.goTo(list); }}>Back to list</a>
    </div>
  );
}
