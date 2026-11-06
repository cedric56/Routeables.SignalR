import { Component, computed, inject, input } from '@angular/core';
import type { Page } from '@routeables/client';
import { Routeables } from '../routeables';
import type { State } from '../routeables';

@Component({
  selector: 'app-about',
  template: `
    <div class="about-page">
      <div class="about-logo"><img src="/images/logo.svg" alt="Routeables logo" /></div>
      <h1><span class="app-name">Routeables Animals</span><span class="app-version">1.0</span></h1>
      <section class="about-group">
        <h2>Links</h2>
        <div class="actions">
          <a class="action-button" href="https://github.com/cedric56/Routeables" target="_blank" rel="noopener noreferrer">Learn more</a>
          <button type="button" (click)="routeables.run(target(), s().polarBearsCommand)">Polar bears</button>
        </div>
      </section>
    </div>
  `,
})
export class AboutPage {
  readonly page = input.required<Page>();
  readonly target = input('');
  protected readonly routeables = inject(Routeables);
  protected readonly s = computed(() => this.page().state as State);
}
