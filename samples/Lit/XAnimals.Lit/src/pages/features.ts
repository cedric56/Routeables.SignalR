import { html } from 'lit';
import type { Page } from '@routeables/client';
import { routeables } from '../routeables';
import type { State } from '../routeables';

export const features = (page: Page, target: string) => {
  const s = page.state as State;
  const run = (command: string): void => void routeables.run(target, s[command]);
  return html`
    <div class="about-page">
      <section class="about-group">
        <h2>Guard and return</h2>
        <p>The Members page is for members. Signed out, opening it brings you here; sign in, then open it.</p>
        <div class="actions">
          <button type="button" ?disabled=${!s.isSignedOut} @click=${() => run('signInCommand')}>Sign in</button>
          <button type="button" ?disabled=${!s.isSignedIn} @click=${() => run('signOutCommand')}>Sign out</button>
          <button type="button" @click=${() => run('openMembersCommand')}>Open Members</button>
        </div>
      </section>

      <section class="about-group">
        <h2>Link</h2>
        <p>An address from outside, followed through the router. In a browser the address bar does the same job.</p>
        <div class="actions">
          <button type="button" @click=${() => run('openLinkCommand')}>Open ${s.link}</button>
        </div>
      </section>

      <section class="about-group">
        <h2>Lock</h2>
        <p>The page publishes a lock message; the menu entries are disabled until it publishes the unlock one.</p>
        <div class="actions">
          <button type="button" ?disabled=${!s.canLock} @click=${() => run('lockCommand')}>Lock</button>
          <button type="button" ?disabled=${!s.canUnlock} @click=${() => run('unlockCommand')}>Unlock</button>
          <button type="button" ?disabled=${!s.canLock} @click=${() => run('lockForCommand')}>Lock for 5 seconds</button>
        </div>
        <p class="about-result" role="status">${s.lockStatus}</p>
      </section>

      <section class="about-group">
        <h2>Measures</h2>
        <div class="actions">
          <button type="button" @click=${() => run('refreshCommand')}>Show the last route changes</button>
        </div>
        <pre>${s.measures}</pre>
      </section>

      <p class="about-result" role="status">${s.status}</p>
    </div>`;
};
