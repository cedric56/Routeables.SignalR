import { Component, effect, forwardRef, inject, input, signal } from '@angular/core';
import type { Page } from '@routeables/client';
import { PageView } from './page-view';
import { Routeables } from './routeables';

/**
 * The zone of a region: it shows what the router of the region displays. Nothing assigns a page to it; the page is chosen by its
 * view model. A page that arrives plays the transition of the zone again.
 */
@Component({
  selector: 'app-region',
  // PageView draws pages that contain regions (History, Regions, Tabs): the two components need each other.
  imports: [forwardRef(() => PageView)],
  template: `
    @for (p of shown(); track p.sequence) {
      <div [class]="'rt-enter rt-' + transition().toLowerCase()"><app-page [page]="p" [target]="name()" /></div>
    }
  `,
})
export class Region {
  /** The name of the region. */
  readonly name = input.required<string>();
  /** The transition the pages come in with (None, Fade, Slide...). */
  readonly transition = input('None');

  private readonly routeables = inject(Routeables);
  private readonly page = signal<Page | null>(null);
  protected readonly shown = () => (this.page() ? [this.page()!] : []);

  constructor() {
    effect(onCleanup => onCleanup(this.routeables.watch(this.name(), page => this.page.set(page))));
  }
}
