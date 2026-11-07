import type { State } from '../routeables';
import type { PageProps } from '../store';

export function LayoutLogo(props: PageProps) {
  const s = (): State => props.page.state;
  return <div class="rg-logo"><img src="/images/logo.svg" alt={`${s().name} logo`} /></div>;
}
