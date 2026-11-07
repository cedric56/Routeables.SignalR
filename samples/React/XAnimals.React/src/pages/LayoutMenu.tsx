import { routeables } from '../routeables';
import type { State } from '../routeables';
import type { PageProps } from '../hooks';

interface Item { name: string; glyph: string; isCurrent: boolean; select: unknown }

/** The menu of the left panel. It is in a region of its own: its commands are called on that region. */
export function LayoutMenu({ page, target }: PageProps) {
  const s = page.state as State;
  const items = (s.items as Item[] | undefined) ?? [];
  return (
    <nav className="rg-menu">
      <div className="rg-menu-moves">
        <button type="button" disabled={!s.canGoBack} onClick={() => void routeables.run(target, s.backCommand)}>◀ Back</button>
        <button type="button" disabled={!s.canGoForward} onClick={() => void routeables.run(target, s.forwardCommand)}>Forward ▶</button>
      </div>
      {items.map(item => (
        <button key={item.name} type="button" className={`rg-menu-item${item.isCurrent ? ' current' : ''}`}
                aria-current={item.isCurrent ? 'page' : undefined} onClick={() => void routeables.run(target, item.select)}>
          <span className="rg-menu-glyph">{item.glyph}</span>
          <span>{item.name}</span>
        </button>
      ))}
    </nav>
  );
}
