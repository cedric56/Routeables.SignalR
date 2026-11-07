import { createEffect, createSignal, onCleanup } from 'solid-js';
import type { Accessor } from 'solid-js';
import type { Page } from '@routeables/client';
import { routeables } from './routeables';
import type { Snapshot } from './routeables';

// The snapshot as a signal: what reads `snapshot()` follows it. It is replaced, never changed.
const [snapshot, setSnapshot] = createSignal<Snapshot>(routeables.snapshot);
routeables.subscribe(next => setSnapshot(next));

/** What the application shows. */
export { snapshot };

/** What a target shows (a region, `$name`), followed while the component is there. */
export function useTarget(target: Accessor<string>): Accessor<Page | null> {
  const [page, setPage] = createSignal<Page | null>(null);
  createEffect(() => onCleanup(routeables.follow(target(), next => setPage(next))));
  return page;
}

/** What every page component receives: its page, and the target that shows it. */
export interface PageProps {
  page: Page;
  target: string;
}
