import { createEffect, createMemo, For, Show } from 'solid-js';
import { firstSegment, routeables } from './routeables';
import type { MenuEntry, State } from './routeables';
import { snapshot } from './store';
import { Interactions } from './Interactions';
import { PageView } from './PageView';

// A link is a route: the server navigates and the page that arrives replaces this one. No reload.
function open(event: MouseEvent, route: string): void {
  event.preventDefault();
  void routeables.goTo(route);
}

export function App() {
  /** The menu: the server sends it ($shell) and says which entries a lock disables. */
  const entries = createMemo(() => (snapshot().shell?.state['entries'] as MenuEntry[] | undefined) ?? []);
  const theme = createMemo(() => String((snapshot().shell?.state as State | undefined)?.['theme'] ?? 'Default'));
  /** The menu marks the first segment of the address: /cats/Tom is still in Cats. */
  const current = createMemo(() => '/' + firstSegment(snapshot().page?.route ?? ''));
  const isActive = (entry: MenuEntry): boolean => entry.route.toLowerCase() === current().toLowerCase();

  // The theme the view models asked for (IRouteableRequestedThemeService). Default follows the browser.
  createEffect(() => document.documentElement.setAttribute('data-theme', theme().toLowerCase()));
  void routeables.start();

  return (
    <>
      <header class="site-header">
        <button type="button" class="drawer-toggle" aria-controls="drawer" aria-expanded={!snapshot().drawerClosed}
                aria-label="Show or hide the menu" onClick={() => routeables.toggleDrawer()}>☰</button>
        <a class="brand" href="/cats" onClick={e => open(e, '/cats')}>Routeables Animals</a>
        {/* The theme is asked of the view model like on the other platforms; Default follows the browser. */}
        <button type="button" class="drawer-toggle theme-form" title={`Theme: ${theme()} (click for the next one)`}
                aria-label={`Change the theme, now ${theme()}`} onClick={() => void routeables.run('$shell', snapshot().shell?.state['themeCommand'])}>◐</button>
      </header>

      <main>
        <div class="shell" classList={{ 'drawer-closed': snapshot().drawerClosed }}>
          {/* The side panel: one entry per entry of the main view model, as in the other samples. */}
          <nav class="drawer" id="drawer" aria-label="Menu">
            <For each={entries()}>
              {entry => (
                // A page published a lock (Features): the entry cannot be followed until it publishes the unlock.
                <Show when={!entry.isLocked} fallback={
                  <span class="drawer-link locked" title="Locked">
                    <span class="drawer-icon" aria-hidden="true">{entry.glyph}</span>
                    <span class="drawer-name">{entry.name}</span>
                  </span>
                }>
                  <a class="drawer-link" classList={{ active: isActive(entry) }} aria-current={isActive(entry) ? 'page' : undefined}
                     href={entry.route} onClick={e => open(e, entry.route)}>
                    <span class="drawer-icon" aria-hidden="true">{entry.glyph}</span>
                    <span class="drawer-text">
                      <span class="drawer-name">{entry.name}</span>
                      <Show when={entry.description}><span class="drawer-description">{entry.description}</span></Show>
                    </span>
                  </a>
                </Show>
              )}
            </For>
          </nav>

          <div class="shell-content">
            <div style={{ padding: '20px' }}>
              <Show when={snapshot().error}><p class="error" role="alert">{snapshot().error}</p></Show>
              <Show when={snapshot().page} fallback={
                <Show when={snapshot().connected} fallback={<p class="empty">Offline: the server is not reachable.</p>}>
                  <p class="empty">Loading…</p>
                </Show>
              }>
                <PageView page={snapshot().page!} target="" />
              </Show>
            </div>
          </div>
        </div>
      </main>

      <Interactions />
    </>
  );
}
