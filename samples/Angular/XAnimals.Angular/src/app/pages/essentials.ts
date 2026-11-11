import { Component, computed, inject, input } from '@angular/core';
import type { Page } from '@routeables/client';
import { Routeables } from '../routeables';
import type { State } from '../routeables';

/** One test of the page: a device API, called by the server through the Essentials of this client. */
interface Check {
  group: string;
  name: string;
  service: string;
  description: string;
  label: string;
  supported: boolean;
  status: string;
  result: string;
  runCommand: unknown;
}

/** The tests of the page, by group, in the order of the server. */
export function groupChecks(checks: readonly Check[]): { name: string; checks: Check[] }[] {
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
@Component({
  selector: 'app-essentials',
  template: `
    <div class="about-page essentials-page">
      <p class="essentials-summary" role="status">{{ s().summary }}</p>
      @if (s().capabilities?.length) {
        <ul class="essentials-capabilities" aria-label="Announced services">
          @for (capability of s().capabilities; track capability) { <li>{{ capability }}</li> }
        </ul>
      }
      @for (group of groups(); track group.name) {
        <section class="essentials-group">
          <h2>{{ group.name }}</h2>
          @for (check of group.checks; track check.name) {
            <article class="essentials-check" [class.unsupported]="!check.supported">
              <span class="essentials-name">
                {{ check.name }}
                <span [class]="'essentials-status ' + check.status">{{ check.status }}</span>
              </span>
              <p class="essentials-description">{{ check.description }}</p>
              <button type="button" [disabled]="!check.supported || check.status === 'running'" (click)="run(check)">{{ check.label }}</button>
              @if (check.result) { <pre class="essentials-result">{{ check.result }}</pre> }
            </article>
          }
        </section>
      }
    </div>
  `,
})
export class EssentialsPage {
  readonly page = input.required<Page>();
  readonly target = input('');
  private readonly routeables = inject(Routeables);
  protected readonly s = computed(() => this.page().state as State);
  protected readonly groups = computed(() => groupChecks((this.s().checks ?? []) as Check[]));

  protected run(check: Check): void {
    void this.routeables.run(this.target(), check.runCommand);
  }
}
