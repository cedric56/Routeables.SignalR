import { Show } from 'solid-js';
import { firstSegment, routeables } from '../routeables';
import type { State } from '../routeables';
import type { PageProps } from '../store';

interface Animal { name: string; location: string; details: string; photoUrl: string }

export function AnimalDetail(props: PageProps) {
  const animal = (): Animal | null | undefined => (props.page.state as State).animal;
  const list = (): string => '/' + firstSegment(props.page.route);

  return (
    <div class="monkey-detail">
      <Show when={animal()} fallback={<><h1 class="title">Animal not found</h1><p class="body-text">This animal was not found.</p></>}>
        {current => (
          <>
            <h1 class="title">{current().name}</h1>
            <p class="location">{current().location}</p>
            <img src={current().photoUrl} alt={current().name} width={260} height={330} />
            <p class="body-text">{current().details}</p>
          </>
        )}
      </Show>
      <a class="back-btn" href={list()} onClick={e => { e.preventDefault(); void routeables.goTo(list()); }}>Back to list</a>
    </div>
  );
}
