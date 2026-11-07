import { routeables } from '../routeables';
import type { State } from '../routeables';
import type { PageProps } from '../store';
import { Region } from '../Region';

/** An application of its own: four zones, four routers. Each zone shows whatever the router of its region displays. */
export function Regions(props: PageProps) {
  const s = (): State => props.page.state;
  const label = (): string => (s().isLeftCollapsed ? 'Show the panel' : 'Reduce the panel');
  return (
    <div class="rg-app" classList={{ 'left-collapsed': s().isLeftCollapsed }}>
      <header class="rg-header"><Region name="shell-header" /></header>
      <aside class="rg-left">
        <button type="button" class="rg-collapse" title={label()} aria-label={label()} aria-expanded={!s().isLeftCollapsed}
                onClick={() => void routeables.run(props.target, s().toggleLeftCommand)}>{s().isLeftCollapsed ? '»' : '«'}</button>
        <div class="rg-left-region"><Region name="shell-left" /></div>
        {/* One navigation made of segments: the logo in the content, the author in the footer, the copyright in the left panel. */}
        <button type="button" class="rg-credits" onClick={() => void routeables.run(props.target, s().creditsCommand)}>{s().creditsText}</button>
      </aside>
      <main class="rg-content"><Region name="shell-content" /></main>
      <footer class="rg-footer"><Region name="shell-footer" /></footer>
    </div>
  );
}
