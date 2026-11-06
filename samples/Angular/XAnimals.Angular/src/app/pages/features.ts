import { Component, computed, inject, input } from '@angular/core';
import type { Page } from '@routeables/client';
import { Routeables } from '../routeables';
import type { State } from '../routeables';

@Component({
  selector: 'app-features',
  template: `
    <div class="about-page">
      <section class="about-group">
        <h2>Guard and return</h2>
        <p>The Members page is for members. Signed out, opening it brings you here; sign in, then open it.</p>
        <div class="actions">
          <button type="button" [disabled]="!s().isSignedOut" (click)="run('signInCommand')">Sign in</button>
          <button type="button" [disabled]="!s().isSignedIn" (click)="run('signOutCommand')">Sign out</button>
          <button type="button" (click)="run('openMembersCommand')">Open Members</button>
        </div>
      </section>

      <section class="about-group">
        <h2>Link</h2>
        <p>An address from outside, followed through the router. In a browser the address bar does the same job.</p>
        <div class="actions">
          <button type="button" (click)="run('openLinkCommand')">Open {{ s().link }}</button>
        </div>
      </section>

      <section class="about-group">
        <h2>Lock</h2>
        <p>The page publishes a lock message; the menu entries are disabled until it publishes the unlock one.</p>
        <div class="actions">
          <button type="button" [disabled]="!s().canLock" (click)="run('lockCommand')">Lock</button>
          <button type="button" [disabled]="!s().canUnlock" (click)="run('unlockCommand')">Unlock</button>
          <button type="button" [disabled]="!s().canLock" (click)="run('lockForCommand')">Lock for 5 seconds</button>
        </div>
        <p class="about-result" role="status">{{ s().lockStatus }}</p>
      </section>

      <section class="about-group">
        <h2>Measures</h2>
        <div class="actions">
          <button type="button" (click)="run('refreshCommand')">Show the last route changes</button>
        </div>
        <pre>{{ s().measures }}</pre>
      </section>

      <p class="about-result" role="status">{{ s().status }}</p>
    </div>
  `,
})
export class FeaturesPage {
  readonly page = input.required<Page>();
  readonly target = input('');
  private readonly routeables = inject(Routeables);
  protected readonly s = computed(() => this.page().state as State);

  protected run(command: string): void {
    void this.routeables.run(this.target(), this.s()[command]);
  }
}
