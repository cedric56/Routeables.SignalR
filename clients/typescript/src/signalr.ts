import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import type { HubConnection, IHttpConnectionOptions } from '@microsoft/signalr';
import type { Ack, RouteableTransport, ServerMessages } from './types.js';

export interface SignalROptions extends IHttpConnectionOptions {
  /** The log level of SignalR. Warning by default. */
  logLevel?: LogLevel;
}

/** The transport over SignalR: the hub `MapRouteables("/routeables")` of Routeables.SignalR. */
export function signalRTransport(url = '/routeables', options: SignalROptions = {}): RouteableTransport {
  const { logLevel, ...http } = options;
  const connection: HubConnection = new HubConnectionBuilder()
    .withUrl(url, http)
    // The connection is the lifetime of the view models on the server: a reconnection is a new connection.
    // The client says so (onConnectionChange) and the application goes to its route again.
    .withAutomaticReconnect()
    .configureLogging(logLevel ?? LogLevel.Warning)
    .build();

  return {
    async start(handlers: ServerMessages): Promise<void> {
      connection.on('Navigated', handlers.navigated);
      connection.on('Changed', handlers.changed);
      connection.on('Failed', handlers.failed);
      connection.on('DialogRequested', handlers.dialogRequested);
      connection.on('Notified', handlers.notified);
      connection.on('Closed', handlers.closed);
      await connection.start();
    },

    stop: () => connection.stop(),

    call(method, ...args): Promise<Ack> {
      if (connection.state !== HubConnectionState.Connected)
        return Promise.resolve({ success: false, error: 'Not connected.' });
      return connection.invoke<Ack>(method, ...args);
    },

    onConnectionChange(handler): void {
      connection.onreconnecting(() => handler(false));
      connection.onreconnected(() => handler(true));
      connection.onclose(() => handler(false));
    },
  };
}
