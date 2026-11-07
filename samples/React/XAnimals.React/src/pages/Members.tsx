import { routeables } from '../routeables';
import type { State } from '../routeables';
import type { PageProps } from '../hooks';

export function Members({ page, target }: PageProps) {
  const s = page.state as State;
  return (
    <div className="about-page">
      <div className="about-group">
        <p>{s.welcome}</p>
        <div className="actions">
          <button type="button" onClick={() => void routeables.run(target, s.signOutCommand)}>Sign out</button>
        </div>
      </div>
    </div>
  );
}
