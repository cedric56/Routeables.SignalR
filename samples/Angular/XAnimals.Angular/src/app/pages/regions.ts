import { Component, computed, inject, input } from '@angular/core';
import type { Page } from '@routeables/client';
import { Region } from '../region';
import { Routeables } from '../routeables';
import type { State } from '../routeables';

/** An application of its own: four zones, four routers. Each zone shows whatever the router of its region displays. */
@Component({
  selector: 'app-regions',
  imports: [Region],
  template: `
    <div class="rg-app" [class.left-collapsed]="s().isLeftCollapsed">
      <header class="rg-header"><app-region name="shell-header" /></header>
      <aside class="rg-left">
        <button type="button" class="rg-collapse" (click)="toggleLeft()"
                [title]="s().isLeftCollapsed ? 'Show the panel' : 'Reduce the panel'"
                [attr.aria-label]="s().isLeftCollapsed ? 'Show the panel' : 'Reduce the panel'"
                [attr.aria-expanded]="!s().isLeftCollapsed">{{ s().isLeftCollapsed ? '»' : '«' }}</button>
        <div class="rg-left-region"><app-region name="shell-left" /></div>
        <!-- One navigation made of segments: the logo in the content, the author in the footer, the copyright in the left panel. -->
        <button type="button" class="rg-credits" (click)="routeables.run(target(), s().creditsCommand)">{{ s().creditsText }}</button>
      </aside>
      <main class="rg-content"><app-region name="shell-content" /></main>
      <footer class="rg-footer"><app-region name="shell-footer" /></footer>
    </div>
  `,
})
export class RegionsPage {
  readonly page = input.required<Page>();
  readonly target = input('');
  protected readonly routeables = inject(Routeables);
  protected readonly s = computed(() => this.page().state as State);

  protected toggleLeft(): void {
    void this.routeables.run(this.target(), this.s()['toggleLeftCommand']);
  }
}

@Component({
  selector: 'app-layout-bar',
  template: `
    <div class="rg-bar" [class.strong]="s().isStrong">
      <span class="rg-bar-glyph">{{ s().glyph }}</span>
      <div class="rg-bar-text">
        <strong>{{ s().title }}</strong>
        <span>{{ s().detail }}</span>
      </div>
    </div>
  `,
})
export class LayoutBar {
  readonly page = input.required<Page>();
  protected readonly s = computed(() => this.page().state as State);
}

/** The menu of the left panel. It is in a region of its own: its commands are called on that region. */
@Component({
  selector: 'app-layout-menu',
  template: `
    <nav class="rg-menu">
      <div class="rg-menu-moves">
        <button type="button" [disabled]="!s().canGoBack" (click)="routeables.run(target(), s().backCommand)">◀ Back</button>
        <button type="button" [disabled]="!s().canGoForward" (click)="routeables.run(target(), s().forwardCommand)">Forward ▶</button>
      </div>
      @for (item of items(); track item.name) {
        <button type="button" class="rg-menu-item" [class.current]="item.isCurrent"
                [attr.aria-current]="item.isCurrent ? 'page' : null" (click)="routeables.run(target(), item.select)">
          <span class="rg-menu-glyph">{{ item.glyph }}</span>
          <span>{{ item.name }}</span>
        </button>
      }
    </nav>
  `,
})
export class LayoutMenu {
  readonly page = input.required<Page>();
  readonly target = input('');
  protected readonly routeables = inject(Routeables);
  protected readonly s = computed(() => this.page().state as State);
  protected readonly items = computed(() => (this.s()['items'] as { name: string; glyph: string; isCurrent: boolean; select: unknown }[] | undefined) ?? []);
}

@Component({
  selector: 'app-layout-logo',
  template: `<div class="rg-logo"><img src="/images/logo.svg" [alt]="s().name + ' logo'" /></div>`,
})
export class LayoutLogo {
  readonly page = input.required<Page>();
  protected readonly s = computed(() => this.page().state as State);
}
