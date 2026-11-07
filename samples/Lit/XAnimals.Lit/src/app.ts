import { html, nothing } from 'lit';
import { customElement } from 'lit/decorators.js';
import { classMap } from 'lit/directives/class-map.js';
import { repeat } from 'lit/directives/repeat.js';
import { firstSegment, routeables } from './routeables';
import type { MenuEntry, State } from './routeables';
import { LightElement, SnapshotController } from './base';
import './interactions';
import './page-view';

// A link is a route: the server navigates and the page that arrives replaces this one. No reload.
function open(event: Event, route: string): void {
  event.preventDefault();
  void routeables.goTo(route);
}

/** The application: the header, the side panel, the page of the main router, and what the services of the server show. */
@customElement('xa-app')
export class App extends LightElement {
  private readonly app = new SnapshotController(this);

  override connectedCallback(): void {
    super.connectedCallback();
    void routeables.start();
  }

  protected override render() {
    const { page, shell, drawerClosed, connected, error } = this.app.value;
    /** The menu: the server sends it ($shell) and says which entries a lock disables. */
    const entries = (shell?.state['entries'] as MenuEntry[] | undefined) ?? [];
    const theme = String((shell?.state as State | undefined)?.['theme'] ?? 'Default');
    /** The menu marks the first segment of the address: /cats/Tom is still in Cats. */
    const current = '/' + firstSegment(page?.route ?? '');
    const isActive = (entry: MenuEntry): boolean => entry.route.toLowerCase() === current.toLowerCase();

    // The theme the view models asked for (IRouteableRequestedThemeService). Default follows the browser.
    document.documentElement.setAttribute('data-theme', theme.toLowerCase());

    return html`
      <header class="site-header">
        <button type="button" class="drawer-toggle" aria-controls="drawer" aria-expanded=${String(!drawerClosed)}
                aria-label="Show or hide the menu" @click=${() => routeables.toggleDrawer()}>☰</button>
        <a class="brand" href="/cats" @click=${(e: Event) => open(e, '/cats')}>Routeables Animals</a>
        <!-- The theme is asked of the view model like on the other platforms; Default follows the browser. -->
        <button type="button" class="drawer-toggle theme-form" title="Theme: ${theme} (click for the next one)"
                aria-label="Change the theme, now ${theme}" @click=${() => routeables.run('$shell', shell?.state['themeCommand'])}>◐</button>
      </header>

      <main>
        <div class=${classMap({ shell: true, 'drawer-closed': drawerClosed })}>
          <!-- The side panel: one entry per entry of the main view model, as in the other samples. -->
          <nav class="drawer" id="drawer" aria-label="Menu">
            ${repeat(entries, entry => entry.route, entry => entry.isLocked
              // A page published a lock (Features): the entry cannot be followed until it publishes the unlock.
              ? html`
                <span class="drawer-link locked" title="Locked">
                  <span class="drawer-icon" aria-hidden="true">${entry.glyph}</span>
                  <span class="drawer-name">${entry.name}</span>
                </span>`
              : html`
                <a class=${classMap({ 'drawer-link': true, active: isActive(entry) })} aria-current=${isActive(entry) ? 'page' : nothing}
                   href=${entry.route} @click=${(e: Event) => open(e, entry.route)}>
                  <span class="drawer-icon" aria-hidden="true">${entry.glyph}</span>
                  <span class="drawer-text">
                    <span class="drawer-name">${entry.name}</span>
                    ${entry.description ? html`<span class="drawer-description">${entry.description}</span>` : nothing}
                  </span>
                </a>`)}
          </nav>

          <div class="shell-content">
            <div style="padding: 20px">
              ${error ? html`<p class="error" role="alert">${error}</p>` : nothing}
              ${page ? html`<xa-page-view .page=${page}></xa-page-view>`
                : connected ? html`<p class="empty">Loading…</p>` : html`<p class="empty">Offline: the server is not reachable.</p>`}
            </div>
          </div>
        </div>
      </main>

      <xa-interactions></xa-interactions>`;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'xa-app': App;
  }
}
