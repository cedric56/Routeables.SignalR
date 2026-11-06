import { Component, computed, inject, input } from '@angular/core';
import type { Page } from '@routeables/client';
import { Region } from '../region';
import { Routeables } from '../routeables';
import type { State } from '../routeables';

interface Entry {
  number: number;
  name: string;
  glyph: string;
  caption: string;
  badge: string;
  isCurrent: boolean;
  isAhead: boolean;
}

/** The history of a region, as cards, and the frame that shows the page the region displays. */
@Component({
  selector: 'app-history',
  imports: [Region],
  template: `
    <div class="about-page">
      <section class="about-group">
        <div class="nav-track" role="list">
          @for (entry of entries(); track entry.number) {
            <div class="nav-card" [class.current]="entry.isCurrent" [class.ahead]="entry.isAhead"
                 [class.behind]="!entry.isCurrent && !entry.isAhead" role="listitem">
              <span class="nav-number">{{ entry.number }}</span>
              <span class="nav-badge">{{ entry.badge }}</span>
              <span class="nav-glyph">{{ entry.glyph }}</span>
              <span class="nav-name">{{ entry.name }}</span>
              <span class="nav-caption">{{ entry.caption }}</span>
            </div>
          }
        </div>
      </section>

      <section class="about-group">
        <!-- The zone of the "history" region: nothing assigns the page, it shows what the router of the region displays. -->
        <div class="nav-frame" inert><app-region name="history" [transition]="s().transition" /></div>
      </section>

      <section class="about-group">
        <div class="actions">
          <button type="button" (click)="run('pushCommand')">＋ Push</button>
          <button type="button" [disabled]="!s().canGoBack" (click)="run('backCommand')">◀ Back</button>
          <button type="button" [disabled]="!s().canGoForward" (click)="run('forwardCommand')">Forward ▶</button>
          <button type="button" [disabled]="!s().canGoBack" (click)="run('popCommand')">↩ Pop</button>
        </div>
      </section>

      <section class="about-group">
        <h2>Transition</h2>
        <select aria-label="Transition" (change)="transition($event)">
          @for (name of transitions(); track name) {
            <option [value]="name" [selected]="name === s().transition">{{ name }}</option>
          }
        </select>
      </section>

      <section class="about-group">
        <h2>CanLeaveAsync</h2>
        <button type="button" class="nav-check" (click)="block()">{{ s().guardGlyph }} {{ s().guardText }}</button>
      </section>

      <p class="about-result nav-status" [class.refused]="s().isRefused" role="status">{{ s().isRefused ? '🚫 ' : '' }}{{ s().status }}</p>
    </div>
  `,
})
export class HistoryPage {
  readonly page = input.required<Page>();
  readonly target = input('');
  private readonly routeables = inject(Routeables);
  protected readonly s = computed(() => this.page().state as State);
  protected readonly entries = computed(() => (this.s()['entries'] as Entry[] | undefined) ?? []);
  protected readonly transitions = computed(() => (this.s()['transitions'] as string[] | undefined) ?? []);

  protected run(command: string): void {
    void this.routeables.run(this.target(), this.s()[command]);
  }

  protected transition(event: Event): void {
    void this.routeables.set(this.target(), 'transition', (event.target as HTMLSelectElement).value);
  }

  protected block(): void {
    void this.routeables.set(this.target(), 'blockLeaving', !this.s()['blockLeaving']);
  }
}
