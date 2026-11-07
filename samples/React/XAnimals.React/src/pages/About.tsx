import { routeables } from '../routeables';
import type { State } from '../routeables';
import type { PageProps } from '../hooks';

export function About({ page, target }: PageProps) {
  const s = page.state as State;
  return (
    <div className="about-page">
      <div className="about-logo"><img src="/images/logo.svg" alt="Routeables logo" /></div>
      <h1><span className="app-name">Routeables Animals</span><span className="app-version">1.0</span></h1>
      <section className="about-group">
        <h2>Links</h2>
        <div className="actions">
          <a className="action-button" href="https://github.com/cedric56/Routeables" target="_blank" rel="noopener noreferrer">Learn more</a>
          <button type="button" onClick={() => void routeables.run(target, s.polarBearsCommand)}>Polar bears</button>
        </div>
      </section>
    </div>
  );
}
