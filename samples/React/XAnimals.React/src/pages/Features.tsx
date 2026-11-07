import { routeables } from '../routeables';
import type { State } from '../routeables';
import type { PageProps } from '../hooks';

export function Features({ page, target }: PageProps) {
  const s = page.state as State;
  const run = (command: string): void => void routeables.run(target, s[command]);
  return (
    <div className="about-page">
      <section className="about-group">
        <h2>Guard and return</h2>
        <p>The Members page is for members. Signed out, opening it brings you here; sign in, then open it.</p>
        <div className="actions">
          <button type="button" disabled={!s.isSignedOut} onClick={() => run('signInCommand')}>Sign in</button>
          <button type="button" disabled={!s.isSignedIn} onClick={() => run('signOutCommand')}>Sign out</button>
          <button type="button" onClick={() => run('openMembersCommand')}>Open Members</button>
        </div>
      </section>

      <section className="about-group">
        <h2>Link</h2>
        <p>An address from outside, followed through the router. In a browser the address bar does the same job.</p>
        <div className="actions">
          <button type="button" onClick={() => run('openLinkCommand')}>Open {s.link}</button>
        </div>
      </section>

      <section className="about-group">
        <h2>Lock</h2>
        <p>The page publishes a lock message; the menu entries are disabled until it publishes the unlock one.</p>
        <div className="actions">
          <button type="button" disabled={!s.canLock} onClick={() => run('lockCommand')}>Lock</button>
          <button type="button" disabled={!s.canUnlock} onClick={() => run('unlockCommand')}>Unlock</button>
          <button type="button" disabled={!s.canLock} onClick={() => run('lockForCommand')}>Lock for 5 seconds</button>
        </div>
        <p className="about-result" role="status">{s.lockStatus}</p>
      </section>

      <section className="about-group">
        <h2>Measures</h2>
        <div className="actions">
          <button type="button" onClick={() => run('refreshCommand')}>Show the last route changes</button>
        </div>
        <pre>{s.measures}</pre>
      </section>

      <p className="about-result" role="status">{s.status}</p>
    </div>
  );
}
