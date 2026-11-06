import { Component, computed, effect, inject } from '@angular/core';
import { Interactions } from './interactions';
import { PageView } from './page-view';
import { Routeables } from './routeables';
import type { State } from './routeables';

interface MenuEntry {
  name: string;
  route: string;
  glyph: string;
  description: string;
  isLocked: boolean;
}

@Component({
  selector: 'app-root',
  imports: [PageView, Interactions],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App {
  protected readonly routeables = inject(Routeables);
  protected readonly page = this.routeables.page;
  protected readonly closed = this.routeables.drawerClosed;

  /** The menu: the server sends it ($shell) and says which entries a lock disables. */
  protected readonly entries = computed(() => (this.routeables.shell()?.state['entries'] as MenuEntry[] | undefined) ?? []);
  protected readonly theme = computed(() => String((this.routeables.shell()?.state as State | undefined)?.['theme'] ?? 'Default'));

  /** The menu marks the first segment of the address: /cats/Tom is still in Cats. */
  protected readonly current = computed(() => '/' + (this.page()?.route.split('/').filter(Boolean)[0] ?? ''));

  constructor() {
    // The theme the view models asked for (IRouteableRequestedThemeService). Default follows the browser.
    effect(() => document.documentElement.setAttribute('data-theme', this.theme().toLowerCase()));
    void this.routeables.start();
  }

  protected open(event: Event, route: string): void {
    // A link is a route: the server navigates and the page that arrives replaces this one. No reload.
    event.preventDefault();
    void this.routeables.goTo(route);
  }

  protected isActive(entry: MenuEntry): boolean {
    return entry.route.toLowerCase() === this.current().toLowerCase();
  }

  protected cycleTheme(): void {
    void this.routeables.run('$shell', this.routeables.shell()?.state['themeCommand']);
  }
}
