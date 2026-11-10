import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import type { HubConnection, IHttpConnectionOptions, IRetryPolicy } from '@microsoft/signalr';
import type { RouteableTransport, ServerMessages } from './types.js';

export interface SignalROptions extends IHttpConnectionOptions {
  /** The log level of SignalR. Warning by default. */
  logLevel?: LogLevel;
  /** When to try again after the connection dropped. SignalR's default: 0, 2, 10 and 30 seconds. */
  reconnect?: number[] | IRetryPolicy;
}

/** The transport over SignalR: the hub `MapRouteables("/routeables")` of Routeables.SignalR. */
export function signalRTransport(url = '/routeables', options: SignalROptions = {}): RouteableTransport {
  const { logLevel, reconnect, ...http } = options;
  const builder = new HubConnectionBuilder().withUrl(url, http).configureLogging(logLevel ?? LogLevel.Warning);
  // A dropped connection is retried; the client then resumes its session on the server (Hello with its token), within the grace period of the server.
  if (Array.isArray(reconnect)) builder.withAutomaticReconnect(reconnect);
  else if (reconnect) builder.withAutomaticReconnect(reconnect);
  else builder.withAutomaticReconnect();
  const connection: HubConnection = builder.build();
  let wired = false;

  return {
    async start(handlers: ServerMessages): Promise<void> {
      if (!wired) {
        wired = true;
        connection.on('Navigated', handlers.navigated);
        connection.on('Changed', handlers.changed);
        connection.on('Failed', handlers.failed);
        connection.on('DialogRequested', handlers.dialogRequested);
        connection.on('Notified', handlers.notified);
        connection.on('Closed', handlers.closed);
        connection.on('Completed', handlers.completed);
        if (handlers.essentialsInvoke) connection.on('EssentialsInvoke', handlers.essentialsInvoke);
        if (handlers.essentialsSubscribe) connection.on('EssentialsSubscribe', handlers.essentialsSubscribe);
      }
      if (connection.state === HubConnectionState.Disconnected) await connection.start();
    },

    stop: () => connection.stop(),

    call<T>(method: Parameters<RouteableTransport['call']>[0], ...args: unknown[]): Promise<T> {
      if (connection.state !== HubConnectionState.Connected) return Promise.reject(new Error('Not connected.'));
      return connection.invoke<T>(method, ...args);
    },

    onConnectionChange(handler): void {
      connection.onreconnecting(() => handler(false));
      connection.onreconnected(() => handler(true));
      connection.onclose(() => handler(false));
    },
  };
}
