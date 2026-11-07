import { routeables } from '../routeables';
import type { State } from '../routeables';
import { useTarget } from '../hooks';
import type { PageProps } from '../hooks';

interface Pet { animal: { photoUrl: string }; name: string; location: string; glyph: string }
interface Choice { name: string; title: string; adoptCommand: unknown }

/** One button per animal: adopting it routes its card into the region "adopt". */
export function Adopt({ page, target }: PageProps) {
  const s = page.state as State;
  // The cards are the history of the region of the adopted animals (`$pets`).
  const petsPage = useTarget('$pets');
  const choices = (s.choices as Choice[] | undefined) ?? [];
  const pets = (petsPage?.state['pets'] as Pet[] | undefined) ?? [];

  return (
    <div className="about-page">
      <section className="about-group adopt-choices">
        {choices.map(choice => (
          <button key={choice.name} type="button" className="adopt-choice" onClick={() => void routeables.run(target, choice.adoptCommand)}>{choice.title}</button>
        ))}
      </section>

      <section className="about-group adopt-bar">
        <p className="nav-status" role="status">{s.status}</p>
        <button type="button" className="adopt-choice" onClick={() => void routeables.run(target, s.releaseCommand)}>Release the last</button>
        <button type="button" className="adopt-choice" onClick={() => void routeables.run(target, s.releaseAllCommand)}>Release all</button>
      </section>

      <div className="adopt-zone" style={{ display: 'flex', flexWrap: 'wrap', gap: '0.8rem' }}>
        {pets.map(pet => (
          <article key={pet.name} className="pet-card">
            <img src={pet.animal.photoUrl} alt={pet.name} width={150} height={190} />
            <strong>{pet.name}</strong>
            <span className="pet-location">{pet.location}</span>
            <span className="pet-glyph">{pet.glyph}</span>
          </article>
        ))}
      </div>
    </div>
  );
}
