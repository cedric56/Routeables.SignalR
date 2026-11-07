import { routeables } from '../routeables';
import type { State } from '../routeables';
import type { PageProps } from '../store';

export function About(props: PageProps) {
  const s = (): State => props.page.state;
  return (
    <div class="about-page">
      <div class="about-logo"><img src="/images/logo.svg" alt="Routeables logo" /></div>
      <h1><span class="app-name">Routeables Animals</span><span class="app-version">1.0</span></h1>
      <section class="about-group">
        <h2>Links</h2>
        <div class="actions">
          <a class="action-button" href="https://github.com/cedric56/Routeables" target="_blank" rel="noopener noreferrer">Learn more</a>
          <button type="button" onClick={() => void routeables.run(props.target, s().polarBearsCommand)}>Polar bears</button>
        </div>
      </section>
    </div>
  );
}
