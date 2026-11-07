import { LitElement } from 'lit';
import type { ReactiveController, ReactiveControllerHost } from 'lit';
import type { Page } from '@routeables/client';
import { routeables } from './routeables';
import type { Snapshot } from './routeables';

/**
 * The elements of the app draw into the page itself, not into a shadow root: the style sheets of the XAnimals samples (public/css) apply
 * to them as to the other web samples.
 */
export class LightElement extends LitElement {
  protected override createRenderRoot(): HTMLElement {
    return this;
  }

  // The element itself draws no box: what it renders is laid out as if it were there alone (a grid, a flex row).
  override connectedCallback(): void {
    super.connectedCallback();
    this.style.display = 'contents';
  }
}

/** Draws the element again at every change of what the application shows. */
export class SnapshotController implements ReactiveController {
  private stop?: () => void;

  constructor(private readonly host: ReactiveControllerHost) {
    host.addController(this);
  }

  get value(): Snapshot {
    return routeables.snapshot;
  }

  hostConnected(): void {
    this.stop = routeables.subscribe(() => this.host.requestUpdate());
  }

  hostDisconnected(): void {
    this.stop?.();
  }
}

/** What a target shows (a region, `$name`), followed while the element is in the page. */
export class TargetController implements ReactiveController {
  private stop?: () => void;
  private followed?: string;
  value: Page | null = null;

  constructor(private readonly host: ReactiveControllerHost, private readonly target: () => string) {
    host.addController(this);
  }

  hostConnected(): void {
    this.follow();
  }

  hostUpdate(): void {
    if (this.followed !== this.target()) this.follow();
  }

  hostDisconnected(): void {
    this.stop?.();
    this.stop = undefined;
    this.followed = undefined;
  }

  private follow(): void {
    this.stop?.();
    this.followed = this.target();
    this.stop = routeables.follow(this.followed, page => {
      this.value = page;
      this.host.requestUpdate();
    });
  }
}
