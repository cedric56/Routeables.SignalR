/** The name of the target that is the page of the main router. */
export const PAGE = '';
/** The name of the target that is the modal view model that is open. */
export const MODAL = '$modal';
/** The version of the messages this client speaks; the server says whether it speaks it too (`Hello`). */
export const PROTOCOL = 2;

/** A page the server made current: what the component that shows it needs. */
export interface Page {
  /** Counts the pages the target showed: it changes when another page arrives, not when the state of this one changes or is sent again. */
  readonly sequence: number;
  /** What shows it: the main page (""), the modal ("$modal"), a region (its name) or a registered view model ("$name"). */
  readonly target: string;
  /** The address of the page. */
  readonly route: string;
  /** The name of the view model type, to choose the component. */
  readonly viewModel: string;
  /** The properties of the view model, by name (camel case). A new object after every change. */
  readonly state: Readonly<Record<string, unknown>>;
  /** The methods the page may call by name (the ones marked [RouteableCommand] that the user may call). */
  readonly commands: readonly string[];
  /** The generation of the page on the server: the changes that belong to it carry it. */
  readonly generation: number;
  /** The last change applied (0 for the state as it arrived). */
  readonly version: number;
}

/**
 * A command of the state (an ICommand of the view model): the value of the property is this token. Give it to `run`
 * to call it, whatever its depth (`adopt`, `choices.2.adoptCommand`).
 */
export interface CommandRef {
  readonly $command: string;
  readonly canExecute: boolean;
}

export function isCommand(value: unknown): value is CommandRef {
  return typeof value === 'object' && value !== null && typeof (value as CommandRef).$command === 'string';
}

/** A dialog the user has to answer. The answer is the index of the button. */
export interface Dialog {
  readonly id: string;
  /** "message", "content" or "task". */
  readonly kind: string;
  readonly title: string;
  readonly header: string | null;
  readonly body: string | null;
  readonly footer: string | null;
  readonly showProgress: boolean;
  readonly buttons: readonly string[];
}

/** A notification. The client closes it after `expirationSeconds` when it has one (`dismiss`). */
export interface Toast {
  readonly id: number;
  readonly title: string;
  readonly message: string;
  /** "Information", "Success", "Warning" or "Error". */
  readonly type: string;
  readonly expirationSeconds: number | null;
}

/** What the server tells the client (the messages do not depend on the transport). */
export interface NavigatedMessage {
  route: string;
  viewModel: string;
  state: Record<string, unknown>;
  commands: string[];
  target?: string;
  /** Grows at each page (or full state sent again) of the target. */
  generation: number;
  /** The view model object: the same when the state of the same page is sent again. */
  instance: number;
}

/** One change of a state: `set` replaces the value at `path`; `splice` removes `remove` items at `index` of the array at `path` and inserts `value`. */
export interface Patch {
  op: 'set' | 'splice';
  path: (string | number)[];
  value?: unknown;
  index?: number | null;
  remove?: number | null;
}

export interface ChangedMessage {
  target: string;
  generation: number;
  /** 1 for the first change after the page, then one more each time: a hole means a change was lost. */
  version: number;
  patches: Patch[];
}

export interface ClosedMessage {
  target: string;
  generation?: number;
}

export interface FailureMessage {
  route: string;
  message: string;
  target?: string | null;
  code?: string | null;
}

export interface CompletedMessage {
  operation: string;
  success: boolean;
  error?: string | null;
  code?: string | null;
}

/** The answer of the server to a call. */
export interface Ack {
  success: boolean;
  error?: string | null;
  /**
   * What kind of failure: "not-found", "unauthorized", "invalid-argument", "failed", "cancelled", "limit", "disabled", "protocol", "lost" when the server
   * forgot the end of a call the client waited for across a reconnection (the call may have run), "transport" when the call did not reach the server,
   * or "session-lost" when the session ended before the call.
   */
  code?: string | null;
  /** The call went on after the server answered (a dialog, a long command): `Completed` tells its end. The client waits for it. */
  pending?: boolean;
  operation?: string | null;
}

/** What the server answers to `Hello`. */
export interface Welcome {
  success: boolean;
  protocol: number;
  session: string | null;
  resumed: boolean;
  error?: string | null;
}

/** The server messages a transport delivers. */
export interface ServerMessages {
  navigated(message: NavigatedMessage): void;
  changed(message: ChangedMessage): void;
  failed(message: FailureMessage): void;
  dialogRequested(message: Dialog): void;
  notified(message: Toast): void;
  closed(message: ClosedMessage): void;
  completed(message: CompletedMessage): void;
}

/** The methods of the server a transport calls. */
export type ServerMethod =
  | 'Hello'
  | 'GoTo' | 'Invoke' | 'Set'
  | 'Watch' | 'Unwatch' | 'Resync' | 'GoToIn' | 'InvokeOn' | 'SetOn'
  | 'Answer' | 'Dismiss';

/**
 * What the client needs from a connection. SignalR is the first one (see signalr.ts); a raw WebSocket can replace it
 * without touching the client.
 */
export interface RouteableTransport {
  /** Opens the connection and delivers the server messages to <paramref name="handlers"/>. */
  start(handlers: ServerMessages): Promise<void>;
  /** Closes the connection. */
  stop(): Promise<void>;
  /** Calls a method of the server; rejects when the call does not reach it. */
  call<T = Ack>(method: ServerMethod, ...args: unknown[]): Promise<T>;
  /** Called with false when the connection drops (or closes), with true when it comes back. */
  onConnectionChange(handler: (connected: boolean) => void): void;
}
