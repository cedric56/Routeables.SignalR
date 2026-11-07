import { canRun, dialogGroups, routeables } from '../routeables';
import type { State } from '../routeables';
import type { PageProps } from '../hooks';

/**
 * The buttons that open the dialogs and the notifications. A dialog goes to the browser (see Interactions) and the view model waits
 * for the answer on the server, like it waits for a window on a desktop.
 */
export function Dialogs({ page, target }: PageProps) {
  const s = page.state as State;
  return (
    <div className="about-page">
      {dialogGroups.map(group => (
        <section key={group.title} className="about-group">
          <h2>{group.title}</h2>
          <div className="actions">
            {group.actions.map(action => (
              <button key={action.command} type="button" disabled={!canRun(s[action.command])}
                      onClick={() => void routeables.run(target, s[action.command])}>{action.label}</button>
            ))}
          </div>
        </section>
      ))}
      {s.hasResult && <p className="about-result" role="status">{s.result}</p>}
    </div>
  );
}
