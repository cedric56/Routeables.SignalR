import { For } from 'solid-js';
import { routeables } from '../routeables';
import type { State } from '../routeables';
import { useTarget } from '../store';
import type { PageProps } from '../store';

interface Pet { animal: { photoUrl: string }; name: string; location: string; glyph: string }
interface Choice { name: string; title: string; adoptCommand: unknown }

/** One button per animal: adopting it routes its card into the region "adopt". */
export function Adopt(props: PageProps) {
  const s = (): State => props.page.state;
  // The cards are the history of the region of the adopted animals (`$pets`).
  const petsPage = useTarget(() => '$pets');
  const choices = (): Choice[] => s().choices ?? [];
  const pets = (): Pet[] => (petsPage()?.state['pets'] as Pet[] | undefined) ?? [];

  return (
    <div class="about-page">
      <section class="about-group adopt-choices">
        <For each={choices()}>
          {choice => <button type="button" class="adopt-choice" onClick={() => void routeables.run(props.target, choice.adoptCommand)}>{choice.title}</button>}
        </For>
      </section>

      <section class="about-group adopt-bar">
        <p class="nav-status" role="status">{s().status}</p>
        <button type="button" class="adopt-choice" onClick={() => void routeables.run(props.target, s().releaseCommand)}>Release the last</button>
        <button type="button" class="adopt-choice" onClick={() => void routeables.run(props.target, s().releaseAllCommand)}>Release all</button>
      </section>

      <div class="adopt-zone" style={{ display: 'flex', 'flex-wrap': 'wrap', gap: '0.8rem' }}>
        <For each={pets()}>
          {pet => (
            <article class="pet-card">
              <img src={pet.animal.photoUrl} alt={pet.name} width={150} height={190} />
              <strong>{pet.name}</strong>
              <span class="pet-location">{pet.location}</span>
              <span class="pet-glyph">{pet.glyph}</span>
            </article>
          )}
        </For>
      </div>
    </div>
  );
}
