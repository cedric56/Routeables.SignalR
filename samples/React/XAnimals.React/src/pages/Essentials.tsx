import { routeables } from '../routeables';
import type { State } from '../routeables';
import type { PageProps } from '../hooks';

/** One test of the page: a device API, called by the server through the Essentials of this client. */
interface Check {
  group: string;
  name: string;
  description: string;
  label: string;
  supported: boolean;
  status: string;
  result: string;
  runCommand: unknown;
}

/** The tests by group, in the order of the server. */
function groupChecks(checks: readonly Check[]): { name: string; checks: Check[] }[] {
  const groups: { name: string; checks: Check[] }[] = [];
  for (const check of checks) {
    const group = groups.find(g => g.name === check.group);
    if (group) group.checks.push(check);
    else groups.push({ name: check.group, checks: [check] });
  }
  return groups;
}

/**
 * The Essentials page: the server calls the device APIs of this browser (the ones `browserEssentials()` gives) with the .NET MAUI
 * contracts, and shows what came back. The page only draws the list: every test runs on the server.
 */
export function Essentials({ page, target }: PageProps) {
  const s = page.state as State;
  const capabilities = (s.capabilities ?? []) as string[];
  return (
    <div className="about-page essentials-page">
      <p className="essentials-summary" role="status">{s.summary}</p>
      {capabilities.length > 0 && (
        <ul className="essentials-capabilities" aria-label="Announced services">
          {capabilities.map(capability => <li key={capability}>{capability}</li>)}
        </ul>
      )}
      {groupChecks((s.checks ?? []) as Check[]).map(group => (
        <section key={group.name} className="essentials-group">
          <h2>{group.name}</h2>
          {group.checks.map(check => (
            <article key={check.name} className={'essentials-check' + (check.supported ? '' : ' unsupported')}>
              <span className="essentials-name">
                {check.name}
                <span className={'essentials-status ' + check.status}>{check.status}</span>
              </span>
              <p className="essentials-description">{check.description}</p>
              <button type="button" disabled={!check.supported || check.status === 'running'}
                onClick={() => void routeables.run(target, check.runCommand)}>{check.label}</button>
              {check.result && <pre className="essentials-result">{check.result}</pre>}
            </article>
          ))}
        </section>
      ))}
    </div>
  );
}
