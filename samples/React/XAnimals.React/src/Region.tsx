import { useTarget } from './hooks';
import { PageView } from './PageView';

/**
 * The zone of a region: it shows what the router of the region displays. Nothing assigns a page to it; the page is chosen by its
 * view model. A page that arrives plays the transition of the zone again (the key is the number of the page).
 */
export function Region({ name, transition = 'None' }: { name: string; transition?: string }) {
  const page = useTarget(name);
  if (!page) return null;
  return (
    <div key={page.sequence} className={`rt-enter rt-${transition.toLowerCase()}`}>
      <PageView page={page} target={name} />
    </div>
  );
}
