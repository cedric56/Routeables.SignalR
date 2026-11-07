import type { State } from '../routeables';
import type { PageProps } from '../hooks';

export function LayoutBar({ page }: PageProps) {
  const s = page.state as State;
  return (
    <div className={`rg-bar${s.isStrong ? ' strong' : ''}`}>
      <span className="rg-bar-glyph">{s.glyph}</span>
      <div className="rg-bar-text">
        <strong>{s.title}</strong>
        <span>{s.detail}</span>
      </div>
    </div>
  );
}
