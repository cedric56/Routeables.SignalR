import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import type { HubConnection } from '@microsoft/signalr';
import type { SignalROptions } from './signalr.js';
import type {
  LogAdvanced, PullRequest, PullResult, PushRequest, PushResult, RemoteHello, RemoteWelcome, ReplicationChannel, SnapshotRequest, SnapshotResult,
} from './remote.js';

/** A channel over the hub `MapRouteablesReplication("/replication")` of Routeables.SignalR. */
export interface SignalRReplicationChannel extends ReplicationChannel {
  readonly connection: HubConnection;
  /** Called when the log of a collection this connection pulled grows: a hint to synchronize, never the data. */
  onLogAdvanced(listener: (advanced: LogAdvanced) => void): () => void;
  /** Connects, when not connected; every call connects first too. */
  start(): Promise<void>;
  stop(): Promise<void>;
}

export function signalRReplicationChannel(url = '/replication', options: SignalROptions = {}): SignalRReplicationChannel {
  const { logLevel, reconnect, ...http } = options;
  const builder = new HubConnectionBuilder().withUrl(url, http).configureLogging(logLevel ?? LogLevel.Warning);
  if (Array.isArray(reconnect)) builder.withAutomaticReconnect(reconnect);
  else if (reconnect) builder.withAutomaticReconnect(reconnect);
  else builder.withAutomaticReconnect();
  const connection = builder.build();
  const listeners = new Set<(advanced: LogAdvanced) => void>();
  connection.on('LogAdvanced', (advanced: LogAdvanced) => listeners.forEach(l => l(advanced)));
  let starting: Promise<void> | null = null;

  async function start(): Promise<void> {
    if (connection.state === HubConnectionState.Connected) return;
    if (connection.state === HubConnectionState.Disconnected) {
      starting ??= connection.start().finally(() => { starting = null; });
      await starting;
      return;
    }
    // Connecting or reconnecting: the call fails now; the replica synchronizes again later, from what it stored.
    if (starting) await starting;
    else throw new Error('Not connected.');
  }

  async function call<T>(method: string, argument: unknown): Promise<T> {
    await start();
    return connection.invoke<T>(method, argument);
  }

  return {
    connection,
    start,
    stop: () => connection.stop(),
    onLogAdvanced(listener) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    hello: (hello: RemoteHello) => call<RemoteWelcome>('Hello', hello),
    push: (request: PushRequest) => call<PushResult>('Push', request),
    pull: (request: PullRequest) => call<PullResult>('Pull', request),
    snapshot: (request: SnapshotRequest) => call<SnapshotResult>('Snapshot', request),
  };
}
