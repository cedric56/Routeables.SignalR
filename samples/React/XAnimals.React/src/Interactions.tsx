import { routeables } from './routeables';
import type { State } from './routeables';
import { useRouteables } from './hooks';

/**
 * What the services of the server put on the screen: the notifications, the dialog the user has to answer, and the modal (a view model of
 * the server, drawn here). The view model that asked waits on the server for the answer.
 */
export function Interactions() {
  const { toasts, dialogs, modal: modalPage } = useRouteables();
  const dialog = dialogs[0];
  const modal = modalPage?.state as State | undefined;

  return (
    <>
      {toasts.length > 0 && (
        <div className="web-toasts" aria-live="polite">
          {toasts.map(toast => (
            <div key={toast.id} className={`web-toast web-toast-${toast.type.toLowerCase()}`}>
              <button type="button" className="web-toast-body" onClick={() => void routeables.dismiss(toast, true)}>
                <strong>{toast.title}</strong>
                <span>{toast.message}</span>
              </button>
              <button type="button" className="web-toast-close" aria-label="Close" onClick={() => void routeables.dismiss(toast, false)}>✕</button>
            </div>
          ))}
        </div>
      )}

      {modal ? (
        <div className="web-dialog-backdrop">
          <div className="web-dialog" role="dialog" aria-modal="true" aria-labelledby="web-dialog-title">
            <h2 id="web-dialog-title">{modal.title}</h2>
            {/* The box keeps what is typed (defaultValue): the server answers each key after the next one may already be typed. */}
            <input key={modalPage!.sequence} className="search-box" placeholder="Write a note" aria-label="Note" defaultValue={modal.note ?? ''}
                   onChange={e => void routeables.set('$modal', 'note', e.target.value)} />
            <div className="actions">
              {(modal.commands as State[]).map(button => (
                <button key={button.name} type="button" disabled={!button.command.canExecute}
                        onClick={() => void routeables.run('$modal', button.command)}>{button.name}</button>
              ))}
            </div>
          </div>
        </div>
      ) : dialog ? (
        <div className="web-dialog-backdrop">
          <div className="web-dialog" role="dialog" aria-modal="true" aria-labelledby="web-dialog-title">
            <h2 id="web-dialog-title">{dialog.title}</h2>
            {dialog.header && <p><strong>{dialog.header}</strong></p>}
            {dialog.body && <p>{dialog.body}</p>}
            {dialog.showProgress && <progress aria-label="Working" />}
            <div className="actions">
              {dialog.buttons.map((button, index) => (
                <button key={index} type="button" onClick={() => void routeables.answer(dialog, index)}>{button}</button>
              ))}
            </div>
            {dialog.footer && <p className="web-dialog-footer">{dialog.footer}</p>}
          </div>
        </div>
      ) : null}
    </>
  );
}
