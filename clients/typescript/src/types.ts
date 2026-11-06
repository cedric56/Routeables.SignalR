/** The name of the target that is the page of the main router. */
export const PAGE = '';
/** The name of the target that is the modal view model that is open. */
export const MODAL = '$modal';

/** A page the server made current: what the component that shows it needs. */
export interface Page {
  /** Counts the pages the target showed: it changes when another page arrives, not when the state of this one changes. */
  readonly sequence: number;
  /** What shows it: the main page (""), the modal ("$modal"), a region (its name) or a registered view model ("$name"). */
  readonly target: string;
  /** The address of the page. */
  readonly route: string;
  /** The name of the view model type, to choose the component. */
  readonly viewModel: string;
  /** The properties of the view model, by name (camel case). A new object after every change. */
  readonly state: Readonly<Record<string, unknown>>;
  /** The methods the page may call by name (the ones marked [RouteableCommand]). */
  readonly commands: readonly string[];
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
}

export interface ChangedMessage {
  property: string;
  value: unknown;
  target?: string;
}

export interface ClosedMessage {
  target: string;
}

export interface FailureMessage {
  route: string;
  message: string;
}

/** The answer of the server to a call. */
export interface Ack {
  success: boolean;
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
}

/** The methods of the server a transport calls. */
export type ServerMethod =
  | 'GoTo' | 'Invoke' | 'Set'
  | 'Watch' | 'Unwatch' | 'GoToIn' | 'InvokeOn' | 'SetOn'
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
  /** Calls a method of the server. */
  call(method: ServerMethod, ...args: unknown[]): Promise<Ack>;
  /** Called when the connection closes without being asked to, then when it comes back. */
  onConnectionChange(handler: (connected: boolean) => void): void;
}
