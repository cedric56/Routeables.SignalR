import { Component, inject, input, OnInit, signal } from '@angular/core';
import type { Page } from '@routeables/client';
import { Region } from '../region';
import { Routeables } from '../routeables';

/** The items of a tab control are routes: each tab shows its route in the region of the page, and the region draws it. */
@Component({
  selector: 'app-tabs',
  imports: [Region],
  template: `
    <div class="about-page">
      <div class="actions" role="tablist">
        @for (tab of tabs; track tab.route) {
          <button type="button" role="tab" class="adopt-choice" [attr.aria-selected]="tab.route === selected()" (click)="select(tab.route)">{{ tab.header }}</button>
        }
      </div>
    </div>
    <div class="about-page">
      <section class="about-group"><app-region name="tabs-demo" /></section>
    </div>
  `,
})
export class TabsPage implements OnInit {
  readonly page = input.required<Page>();
  readonly target = input('');
  private readonly routeables = inject(Routeables);
  protected readonly tabs = [
    { header: 'Cats', route: 'cats' },
    { header: 'Dogs', route: 'dogs' },
    { header: 'Monkeys', route: 'monkeys' },
  ];
  protected readonly selected = signal('');

  // A tab control selects its first item when it appears.
  ngOnInit(): void {
    this.select(this.tabs[0].route);
  }

  protected select(route: string): void {
    this.selected.set(route);
    void this.routeables.goToIn('tabs-demo', route);
  }
}
