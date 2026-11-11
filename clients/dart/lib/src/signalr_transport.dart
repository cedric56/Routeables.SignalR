import 'package:signalr_netcore/signalr_client.dart';

import 'types.dart';

/// The transport over SignalR: the hub `MapRouteables("/routeables")` of Routeables.SignalR, through the SignalR client for Dart
/// (signalr_netcore). [url] is the full address of the hub: `http://localhost:5090/routeables`.
///
/// [accessTokenFactory] gives the token of the user to a hub that requires authorization; [retryDelays] are the delays, in
/// milliseconds, of the automatic reconnection (SignalR's default: 0, 2, 10 and 30 seconds).
class SignalRTransport implements RouteableTransport {
  SignalRTransport(String url, {Future<String> Function()? accessTokenFactory, List<int>? retryDelays})
      : _connection = HubConnectionBuilder()
            .withUrl(url, options: HttpConnectionOptions(accessTokenFactory: accessTokenFactory))
            // A dropped connection is retried; the client then resumes its session on the server (Hello with its token).
            .withAutomaticReconnect(retryDelays: retryDelays)
            .build();

  final HubConnection _connection;
  bool _wired = false;

  @override
  Future<void> start(ServerMessages handlers) async {
    if (!_wired) {
      _wired = true;
      void listen(String method, void Function(Map<String, Object?> message) handler) =>
          _connection.on(method, (arguments) => handler(asMap(arguments == null || arguments.isEmpty ? null : arguments.first)));
      listen('Navigated', handlers.navigated);
      listen('Changed', handlers.changed);
      listen('Failed', handlers.failed);
      listen('DialogRequested', handlers.dialogRequested);
      listen('Notified', handlers.notified);
      listen('Closed', handlers.closed);
      listen('Completed', handlers.completed);
      if (handlers.essentialsInvoke case final invoke?) listen('EssentialsInvoke', invoke);
      if (handlers.essentialsSubscribe case final subscribe?) listen('EssentialsSubscribe', subscribe);
    }
    if (_connection.state == HubConnectionState.Disconnected) await _connection.start();
  }

  @override
  Future<void> stop() => _connection.stop();

  @override
  Future<Object?> call(String method, List<Object?> args) {
    if (_connection.state != HubConnectionState.Connected) return Future.error(StateError('Not connected.'));
    // The SignalR client for Dart takes arguments that are not null: a null value is sent as JSON null inside a list or a map only.
    if (args.contains(null)) return Future.error(ArgumentError('A value sent to the server cannot be null.'));
    return _connection.invoke(method, args: [for (final arg in args) arg!]);
  }

  @override
  void onConnectionChange(void Function(bool connected) handler) {
    _connection.onreconnecting(({Exception? error}) => handler(false));
    _connection.onreconnected(({String? connectionId}) => handler(true));
    _connection.onclose(({Exception? error}) => handler(false));
  }
}
