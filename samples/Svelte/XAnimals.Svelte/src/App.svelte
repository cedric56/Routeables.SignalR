<script lang="ts">
  import { firstSegment, routeables } from './routeables';
  import type { MenuEntry, State } from './routeables';
  import { app } from './app.svelte';
  import Interactions from './Interactions.svelte';
  import PageView from './PageView.svelte';

  /** The menu: the server sends it ($shell) and says which entries a lock disables. */
  const entries = $derived((app.snapshot.shell?.state['entries'] as MenuEntry[] | undefined) ?? []);
  const theme = $derived(String((app.snapshot.shell?.state as State | undefined)?.['theme'] ?? 'Default'));
  /** The menu marks the first segment of the address: /cats/Tom is still in Cats. */
  const current = $derived('/' + firstSegment(app.snapshot.page?.route ?? ''));
  const isActive = (entry: MenuEntry): boolean => entry.route.toLowerCase() === current.toLowerCase();

  // The theme the view models asked for (IRouteableRequestedThemeService). Default follows the browser.
  $effect(() => document.documentElement.setAttribute('data-theme', theme.toLowerCase()));
  void routeables.start();

  // A link is a route: the server navigates and the page that arrives replaces this one. No reload.
  function open(event: Event, route: string): void {
    event.preventDefault();
    void routeables.goTo(route);
  }
</script>

<header class="site-header">
  <button type="button" class="drawer-toggle" aria-controls="drawer" aria-expanded={!app.snapshot.drawerClosed}
          aria-label="Show or hide the menu" onclick={() => routeables.toggleDrawer()}>☰</button>
  <a class="brand" href="/cats" onclick={e => open(e, '/cats')}>Routeables Animals</a>
  <!-- The theme is asked of the view model like on the other platforms; Default follows the browser. -->
  <button type="button" class="drawer-toggle theme-form" title={`Theme: ${theme} (click for the next one)`}
          aria-label={`Change the theme, now ${theme}`} onclick={() => routeables.run('$shell', app.snapshot.shell?.state['themeCommand'])}>◐</button>
</header>

<main>
  <div class="shell" class:drawer-closed={app.snapshot.drawerClosed}>
    <!-- The side panel: one entry per entry of the main view model, as in the other samples. -->
    <nav class="drawer" id="drawer" aria-label="Menu">
      {#each entries as entry (entry.route)}
        {#if entry.isLocked}
          <!-- A page published a lock (Features): the entry cannot be followed until it publishes the unlock. -->
          <span class="drawer-link locked" title="Locked">
            <span class="drawer-icon" aria-hidden="true">{entry.glyph}</span>
            <span class="drawer-name">{entry.name}</span>
          </span>
        {:else}
          <a class="drawer-link" class:active={isActive(entry)} aria-current={isActive(entry) ? 'page' : undefined}
             href={entry.route} onclick={e => open(e, entry.route)}>
            <span class="drawer-icon" aria-hidden="true">{entry.glyph}</span>
            <span class="drawer-text">
              <span class="drawer-name">{entry.name}</span>
              {#if entry.description}<span class="drawer-description">{entry.description}</span>{/if}
            </span>
          </a>
        {/if}
      {/each}
    </nav>

    <div class="shell-content">
      <div style="padding: 20px">
        {#if app.snapshot.error}<p class="error" role="alert">{app.snapshot.error}</p>{/if}
        {#if app.snapshot.page}
          <PageView page={app.snapshot.page} />
        {:else if app.snapshot.connected}
          <p class="empty">Loading…</p>
        {:else}
          <p class="empty">Offline: the server is not reachable.</p>
        {/if}
      </div>
    </div>
  </div>
</main>

<Interactions />
