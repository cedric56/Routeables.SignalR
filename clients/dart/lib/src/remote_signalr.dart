import 'dart:async';

import 'package:signalr_netcore/signalr_client.dart';

import 'remote.dart';

/// A [ReplicationChannel] over the hub `MapRouteablesReplication("/replication")` of Routeables.SignalR. [url] is the full address
/// of the hub: `http://localhost:5090/replication`.
class SignalRReplicationChannel implements ReplicationChannel {
  SignalRReplicationChannel(String url, {Future<String> Function()? accessTokenFactory, List<int>? retryDelays})
      : connection = HubConnectionBuilder()
            .withUrl(url, options: HttpConnectionOptions(accessTokenFactory: accessTokenFactory))
            .withAutomaticReconnect(retryDelays: retryDelays)
            .build() {
    connection.on('LogAdvanced', (arguments) {
      final message = arguments == null || arguments.isEmpty ? null : arguments.first;
      if (message is Map) _advanced.add(message.cast<String, Object?>());
    });
  }

  final HubConnection connection;
  final StreamController<Map<String, Object?>> _advanced = StreamController.broadcast();
  Future<void>? _starting;

  /// `{ collections, position }` when the log of a collection this connection pulled grows: a hint to synchronize, never the data.
  Stream<Map<String, Object?>> get logAdvanced => _advanced.stream;

  /// Connects, when not connected; every call connects first too.
  Future<void> start() async {
    if (connection.state == HubConnectionState.Connected) return;
    if (connection.state == HubConnectionState.Disconnected) {
      final starting = _starting ??= _connect().whenComplete(() => _starting = null);
      await starting;
      return;
    }
    // Connecting or reconnecting: the call fails now; the replica synchronizes again later, from what it stored.
    final starting = _starting;
    if (starting == null) throw StateError('Not connected.');
    await starting;
  }

  Future<void> _connect() async => await connection.start();

  Future<void> stop() async => await connection.stop();

  @override
  Future<Map<String, Object?>> call(String method, Map<String, Object?> argument) async {
    await start();
    final result = await connection.invoke(method, args: [argument]);
    if (result is! Map) throw StateError('$method answered nothing.');
    return result.cast<String, Object?>();
  }
}
