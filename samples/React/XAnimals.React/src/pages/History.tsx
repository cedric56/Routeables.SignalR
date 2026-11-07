import { routeables } from '../routeables';
import type { State } from '../routeables';
import type { PageProps } from '../hooks';
import { Region } from '../Region';

interface Entry { number: number; name: string; glyph: string; caption: string; badge: string; isCurrent: boolean; isAhead: boolean }

/** The history of a region, as cards, and the frame that shows the page the region displays. */
export function History({ page, target }: PageProps) {
  const s = page.state as State;
  const entries = (s.entries as Entry[] | undefined) ?? [];
  const transitions = (s.transitions as string[] | undefined) ?? [];
  const run = (command: string): void => void routeables.run(target, s[command]);

  return (
    <div className="about-page">
      <section className="about-group">
        <div className="nav-track" role="list">
          {entries.map(entry => (
            <div key={entry.number} role="listitem"
                 className={`nav-card${entry.isCurrent ? ' current' : ''}${entry.isAhead ? ' ahead' : ''}${!entry.isCurrent && !entry.isAhead ? ' behind' : ''}`}>
              <span className="nav-number">{entry.number}</span>
              <span className="nav-badge">{entry.badge}</span>
              <span className="nav-glyph">{entry.glyph}</span>
              <span className="nav-name">{entry.name}</span>
              <span className="nav-caption">{entry.caption}</span>
            </div>
          ))}
        </div>
      </section>

      <section className="about-group">
        {/* The zone of the "history" region: nothing assigns the page, it shows what the router of the region displays. */}
        <div className="nav-frame" inert><Region name="history" transition={s.transition} /></div>
      </section>

      <section className="about-group">
        <div className="actions">
          <button type="button" onClick={() => run('pushCommand')}>＋ Push</button>
          <button type="button" disabled={!s.canGoBack} onClick={() => run('backCommand')}>◀ Back</button>
          <button type="button" disabled={!s.canGoForward} onClick={() => run('forwardCommand')}>Forward ▶</button>
          <button type="button" disabled={!s.canGoBack} onClick={() => run('popCommand')}>↩ Pop</button>
        </div>
      </section>

      <section className="about-group">
        <h2>Transition</h2>
        <select aria-label="Transition" value={s.transition} onChange={e => void routeables.set(target, 'transition', e.target.value)}>
          {transitions.map(name => <option key={name} value={name}>{name}</option>)}
        </select>
      </section>

      <section className="about-group">
        <h2>CanLeaveAsync</h2>
        <button type="button" className="nav-check" onClick={() => void routeables.set(target, 'blockLeaving', !s.blockLeaving)}>{s.guardGlyph} {s.guardText}</button>
      </section>

      <p className={`about-result nav-status${s.isRefused ? ' refused' : ''}`} role="status">{s.isRefused ? '🚫 ' : ''}{s.status}</p>
    </div>
  );
}
