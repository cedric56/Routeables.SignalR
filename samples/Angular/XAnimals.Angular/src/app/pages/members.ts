import { Component, computed, inject, input } from '@angular/core';
import type { Page } from '@routeables/client';
import { Routeables } from '../routeables';
import type { State } from '../routeables';

@Component({
  selector: 'app-members',
  template: `
    <div class="about-page">
      <div class="about-group">
        <p>{{ s().welcome }}</p>
        <div class="actions">
          <button type="button" (click)="routeables.run(target(), s().signOutCommand)">Sign out</button>
        </div>
      </div>
    </div>
  `,
})
export class MembersPage {
  readonly page = input.required<Page>();
  readonly target = input('');
  protected readonly routeables = inject(Routeables);
  protected readonly s = computed(() => this.page().state as State);
}
