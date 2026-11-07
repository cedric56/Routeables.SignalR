import { routeables } from '../routeables';
import type { State } from '../routeables';
import type { PageProps } from '../hooks';
import { Region } from '../Region';

/** An application of its own: four zones, four routers. Each zone shows whatever the router of its region displays. */
export function Regions({ page, target }: PageProps) {
  const s = page.state as State;
  const label = s.isLeftCollapsed ? 'Show the panel' : 'Reduce the panel';
  return (
    <div className={`rg-app${s.isLeftCollapsed ? ' left-collapsed' : ''}`}>
      <header className="rg-header"><Region name="shell-header" /></header>
      <aside className="rg-left">
        <button type="button" className="rg-collapse" title={label} aria-label={label} aria-expanded={!s.isLeftCollapsed}
                onClick={() => void routeables.run(target, s.toggleLeftCommand)}>{s.isLeftCollapsed ? '»' : '«'}</button>
        <div className="rg-left-region"><Region name="shell-left" /></div>
        {/* One navigation made of segments: the logo in the content, the author in the footer, the copyright in the left panel. */}
        <button type="button" className="rg-credits" onClick={() => void routeables.run(target, s.creditsCommand)}>{s.creditsText}</button>
      </aside>
      <main className="rg-content"><Region name="shell-content" /></main>
      <footer className="rg-footer"><Region name="shell-footer" /></footer>
    </div>
  );
}
