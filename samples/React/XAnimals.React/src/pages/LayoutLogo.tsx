import type { State } from '../routeables';
import type { PageProps } from '../hooks';

export function LayoutLogo({ page }: PageProps) {
  const s = page.state as State;
  return <div className="rg-logo"><img src="/images/logo.svg" alt={`${s.name} logo`} /></div>;
}
