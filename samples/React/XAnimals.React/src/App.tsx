import { useEffect } from 'react';
import type { MouseEvent } from 'react';
import { firstSegment, routeables } from './routeables';
import type { MenuEntry, State } from './routeables';
import { useRouteables } from './hooks';
import { Interactions } from './Interactions';
import { PageView } from './PageView';

// A link is a route: the server navigates and the page that arrives replaces this one. No reload.
function open(event: MouseEvent, route: string): void {
  event.preventDefault();
  void routeables.goTo(route);
}

export function App() {
  const { page, shell, drawerClosed, connected, error } = useRouteables();

  /** The menu: the server sends it ($shell) and says which entries a lock disables. */
  const entries = (shell?.state['entries'] as MenuEntry[] | undefined) ?? [];
  const theme = String((shell?.state as State | undefined)?.['theme'] ?? 'Default');
  /** The menu marks the first segment of the address: /cats/Tom is still in Cats. */
  const current = '/' + firstSegment(page?.route ?? '');
  const isActive = (entry: MenuEntry): boolean => entry.route.toLowerCase() === current.toLowerCase();

  // The theme the view models asked for (IRouteableRequestedThemeService). Default follows the browser.
  useEffect(() => document.documentElement.setAttribute('data-theme', theme.toLowerCase()), [theme]);
  useEffect(() => void routeables.start(), []);

  return (
    <>
      <header className="site-header">
        <button type="button" className="drawer-toggle" aria-controls="drawer" aria-expanded={!drawerClosed}
                aria-label="Show or hide the menu" onClick={() => routeables.toggleDrawer()}>☰</button>
        <a className="brand" href="/cats" onClick={e => open(e, '/cats')}>Routeables Animals</a>
        {/* The theme is asked of the view model like on the other platforms; Default follows the browser. */}
        <button type="button" className="drawer-toggle theme-form" title={`Theme: ${theme} (click for the next one)`}
                aria-label={`Change the theme, now ${theme}`} onClick={() => void routeables.run('$shell', shell?.state['themeCommand'])}>◐</button>
      </header>

      <main role="main">
        <div className={`shell${drawerClosed ? ' drawer-closed' : ''}`}>
          {/* The side panel: one entry per entry of the main view model, as in the other samples. */}
          <nav className="drawer" id="drawer" aria-label="Menu">
            {entries.map(entry =>
              // A page published a lock (Features): the entry cannot be followed until it publishes the unlock.
              entry.isLocked ? (
                <span key={entry.route} className="drawer-link locked" title="Locked">
                  <span className="drawer-icon" aria-hidden="true">{entry.glyph}</span>
                  <span className="drawer-name">{entry.name}</span>
                </span>
              ) : (
                <a key={entry.route} className={`drawer-link${isActive(entry) ? ' active' : ''}`} aria-current={isActive(entry) ? 'page' : undefined}
                   href={entry.route} onClick={e => open(e, entry.route)}>
                  <span className="drawer-icon" aria-hidden="true">{entry.glyph}</span>
                  <span className="drawer-text">
                    <span className="drawer-name">{entry.name}</span>
                    {entry.description && <span className="drawer-description">{entry.description}</span>}
                  </span>
                </a>
              ))}
          </nav>

          <div className="shell-content">
            <div style={{ padding: 20 }}>
              {error && <p className="error" role="alert">{error}</p>}
              {page ? <PageView page={page} target="" /> : connected ? <p className="empty">Loading…</p> : <p className="empty">Offline: the server is not reachable.</p>}
            </div>
          </div>
        </div>
      </main>

      <Interactions />
    </>
  );
}
