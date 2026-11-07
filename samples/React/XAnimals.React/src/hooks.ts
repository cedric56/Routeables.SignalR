import { useEffect, useState, useSyncExternalStore } from 'react';
import type { Page } from '@routeables/client';
import { routeables } from './routeables';
import type { Snapshot } from './routeables';

/** What the application shows: the component renders again at every change. */
export function useRouteables(): Snapshot {
  return useSyncExternalStore(routeables.subscribe, () => routeables.snapshot);
}

/** What a target shows (a region, `$name`), followed while the component is there. */
export function useTarget(target: string): Page | null {
  const [page, setPage] = useState<Page | null>(null);
  useEffect(() => routeables.follow(target, setPage), [target]);
  return page;
}

/** What every page component receives: its page, and the target that shows it. */
export interface PageProps {
  page: Page;
  target: string;
}
