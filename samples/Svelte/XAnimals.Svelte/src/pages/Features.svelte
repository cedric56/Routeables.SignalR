<script lang="ts">
  import type { Page } from '@routeables/client';
  import { routeables } from '../routeables';
  import type { State } from '../routeables';

  let { page, target = '' }: { page: Page; target?: string } = $props();
  const s = $derived(page.state as State);

  const run = (command: string): void => void routeables.run(target, s[command]);
</script>

<div class="about-page">
  <section class="about-group">
    <h2>Guard and return</h2>
    <p>The Members page is for members. Signed out, opening it brings you here; sign in, then open it.</p>
    <div class="actions">
      <button type="button" disabled={!s.isSignedOut} onclick={() => run('signInCommand')}>Sign in</button>
      <button type="button" disabled={!s.isSignedIn} onclick={() => run('signOutCommand')}>Sign out</button>
      <button type="button" onclick={() => run('openMembersCommand')}>Open Members</button>
    </div>
  </section>

  <section class="about-group">
    <h2>Link</h2>
    <p>An address from outside, followed through the router. In a browser the address bar does the same job.</p>
    <div class="actions">
      <button type="button" onclick={() => run('openLinkCommand')}>Open {s.link}</button>
    </div>
  </section>

  <section class="about-group">
    <h2>Lock</h2>
    <p>The page publishes a lock message; the menu entries are disabled until it publishes the unlock one.</p>
    <div class="actions">
      <button type="button" disabled={!s.canLock} onclick={() => run('lockCommand')}>Lock</button>
      <button type="button" disabled={!s.canUnlock} onclick={() => run('unlockCommand')}>Unlock</button>
      <button type="button" disabled={!s.canLock} onclick={() => run('lockForCommand')}>Lock for 5 seconds</button>
    </div>
    <p class="about-result" role="status">{s.lockStatus}</p>
  </section>

  <section class="about-group">
    <h2>Measures</h2>
    <div class="actions">
      <button type="button" onclick={() => run('refreshCommand')}>Show the last route changes</button>
    </div>
    <pre>{s.measures}</pre>
  </section>

  <p class="about-result" role="status">{s.status}</p>
</div>
