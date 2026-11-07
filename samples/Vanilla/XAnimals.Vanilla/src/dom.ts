import type { Page } from '@routeables/client';

/** What a child of an element may be: a node, a text, or nothing. */
export type Child = Node | string | number | null | undefined | false | Child[];

type Props = Record<string, unknown>;

// Written as attributes; everything else is a property of the element (disabled, value, defaultValue…).
const attributes = /^(aria-|data-)|^(role|for|href|src|alt|title|type|placeholder|width|height|loading|id|target|rel|inert|style|value)$/;

/**
 * Creates an element: `h('button', { class: 'go', onclick: run }, 'Go')`. A key "on…" with a function listens to the event; "class" is the
 * class name; an attribute (aria-*, role, href…) is set when its value is not false, null or undefined; any other key is a property.
 */
export function h<K extends keyof HTMLElementTagNameMap>(tag: K, props: Props | null = null, ...children: Child[]): HTMLElementTagNameMap[K] {
  const element = document.createElement(tag);
  for (const [key, value] of Object.entries(props ?? {})) {
    if (key.startsWith('on') && typeof value === 'function') element.addEventListener(key.slice(2), value as EventListener);
    else if (key === 'class') element.className = String(value ?? '');
    else if (attributes.test(key)) {
      if (value !== false && value !== null && value !== undefined) element.setAttribute(key, value === true ? '' : String(value));
    } else (element as unknown as Props)[key] = value;
  }
  append(element, children);
  return element;
}

function append(parent: Node, children: Child[]): void {
  for (const child of children) {
    if (Array.isArray(child)) append(parent, child);
    else if (child instanceof Node) parent.appendChild(child);
    else if (child !== null && child !== undefined && child !== false) parent.appendChild(document.createTextNode(String(child)));
  }
}

/** Replaces the children of an element. */
export function fill(parent: Element, ...children: Child[]): void {
  parent.replaceChildren();
  append(parent, children);
}

/** The classes whose condition holds: `classes('card', { current: true, ahead: false })` is "card current". */
export const classes = (base: string, flags: Record<string, unknown>): string =>
  [base, ...Object.entries(flags).filter(([, on]) => on).map(([name]) => name)].join(' ');

/**
 * What is drawn for a page: its element, what to do when the state of the page changes (the same page: the same number), and what to do
 * when it goes away (stop following what it follows).
 */
export interface View {
  readonly node: Node;
  update(page: Page): void;
  destroy(): void;
}

/** A view drawn again whole at every change: for a page that holds nothing the user is typing in, nor a region. */
export function redraw(page: Page, render: (page: Page) => Child): View {
  const node = h('div', { style: 'display: contents' });
  fill(node, render(page));
  return { node, update: next => fill(node, render(next)), destroy: () => {} };
}

/** A link that is a route: the server navigates and the page that arrives replaces this one. No reload. */
export function link(className: string, route: string, go: (route: string) => void, ...children: Child[]): HTMLAnchorElement {
  return h('a', { class: className, href: route, onclick: (e: Event) => { e.preventDefault(); go(route); } }, ...children);
}
