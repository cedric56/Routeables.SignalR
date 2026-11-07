import { routeables } from '../routeables';
import type { State } from '../routeables';
import type { PageProps } from '../store';

export function Members(props: PageProps) {
  const s = (): State => props.page.state;
  return (
    <div class="about-page">
      <div class="about-group">
        <p>{s().welcome}</p>
        <div class="actions">
          <button type="button" onClick={() => void routeables.run(props.target, s().signOutCommand)}>Sign out</button>
        </div>
      </div>
    </div>
  );
}
