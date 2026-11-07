import { routeables } from './routeables';
import type { Snapshot } from './routeables';

// The snapshot as a state of Svelte: a component that reads `app.snapshot` is drawn again at every change. It is replaced, never changed.
let snapshot = $state.raw<Snapshot>(routeables.snapshot);
routeables.subscribe(next => (snapshot = next));

/** What the application shows. */
export const app = {
  get snapshot(): Snapshot {
    return snapshot;
  },
};
