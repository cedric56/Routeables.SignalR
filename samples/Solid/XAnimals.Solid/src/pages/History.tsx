import { For } from 'solid-js';
import { routeables } from '../routeables';
import type { State } from '../routeables';
import type { PageProps } from '../store';
import { Region } from '../Region';

interface Entry { number: number; name: string; glyph: string; caption: string; badge: string; isCurrent: boolean; isAhead: boolean }

/** The history of a region, as cards, and the frame that shows the page the region displays. */
export function History(props: PageProps) {
  const s = (): State => props.page.state;
  const entries = (): Entry[] => s().entries ?? [];
  const transitions = (): string[] => s().transitions ?? [];
  const run = (command: string): void => void routeables.run(props.target, s()[command]);

  return (
    <div class="about-page">
      <section class="about-group">
        <div class="nav-track" role="list">
          <For each={entries()}>
            {entry => (
              <div class="nav-card" role="listitem" classList={{ current: entry.isCurrent, ahead: entry.isAhead, behind: !entry.isCurrent && !entry.isAhead }}>
                <span class="nav-number">{entry.number}</span>
                <span class="nav-badge">{entry.badge}</span>
                <span class="nav-glyph">{entry.glyph}</span>
                <span class="nav-name">{entry.name}</span>
                <span class="nav-caption">{entry.caption}</span>
              </div>
            )}
          </For>
        </div>
      </section>

      <section class="about-group">
        {/* The zone of the "history" region: nothing assigns the page, it shows what the router of the region displays. */}
        <div class="nav-frame" inert><Region name="history" transition={s().transition} /></div>
      </section>

      <section class="about-group">
        <div class="actions">
          <button type="button" onClick={() => run('pushCommand')}>＋ Push</button>
          <button type="button" disabled={!s().canGoBack} onClick={() => run('backCommand')}>◀ Back</button>
          <button type="button" disabled={!s().canGoForward} onClick={() => run('forwardCommand')}>Forward ▶</button>
          <button type="button" disabled={!s().canGoBack} onClick={() => run('popCommand')}>↩ Pop</button>
        </div>
      </section>

      <section class="about-group">
        <h2>Transition</h2>
        <select aria-label="Transition" value={s().transition} onChange={e => void routeables.set(props.target, 'transition', e.currentTarget.value)}>
          <For each={transitions()}>{name => <option value={name}>{name}</option>}</For>
        </select>
      </section>

      <section class="about-group">
        <h2>CanLeaveAsync</h2>
        <button type="button" class="nav-check" onClick={() => void routeables.set(props.target, 'blockLeaving', !s().blockLeaving)}>{s().guardGlyph} {s().guardText}</button>
      </section>

      <p class="about-result nav-status" classList={{ refused: s().isRefused }} role="status">{s().isRefused ? '🚫 ' : ''}{s().status}</p>
    </div>
  );
}
