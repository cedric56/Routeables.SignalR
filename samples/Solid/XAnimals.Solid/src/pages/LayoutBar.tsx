import type { State } from '../routeables';
import type { PageProps } from '../store';

export function LayoutBar(props: PageProps) {
  const s = (): State => props.page.state;
  return (
    <div class="rg-bar" classList={{ strong: s().isStrong }}>
      <span class="rg-bar-glyph">{s().glyph}</span>
      <div class="rg-bar-text">
        <strong>{s().title}</strong>
        <span>{s().detail}</span>
      </div>
    </div>
  );
}
