import 'dart:async';

import 'types.dart';

/// The version of the Essentials protocol this client speaks (`EssentialsReady.version`).
const int essentialsProtocol = 1;

/// A method of a device API: called with the arguments the server sent, in order. What it returns (or completes with) is the result,
/// a JSON value; what it throws is the error.
typedef EssentialsMethod = FutureOr<Object?> Function(List<Object?> args);

/// An event of a device API: called with `emit` when the server starts it, returns the way to stop it.
typedef EssentialsEvent = void Function() Function(void Function(Object? payload) emit);

/// One device API this client offers to the server (the `IPreferences`, `IConnectivity`… of the view models). Only what is listed here
/// can be reached: the server names a method or an event, it never runs arbitrary code.
class EssentialsService {
  const EssentialsService({this.state, this.methods = const {}, this.events = const {}});

  /// The state sent with `EssentialsReady`, read again after each reconnection: what the server answers synchronous calls with.
  final FutureOr<Object?> Function()? state;

  /// The methods the server may call, by name.
  final Map<String, EssentialsMethod> methods;

  /// The events the server may ask for, by name.
  final Map<String, EssentialsEvent> events;
}

/// The kinds of failure the server understands (`EssentialsErrorCodes`).
abstract final class EssentialsErrorCodes {
  static const notSupported = 'not-supported';
  static const permissionDenied = 'permission-denied';
  static const notEnabled = 'not-enabled';
  static const timeout = 'timeout';
  static const invalidArgument = 'invalid-argument';
  static const failed = 'failed';
  static const cancelled = 'cancelled';

  static const all = [notSupported, permissionDenied, notEnabled, timeout, invalidArgument, failed, cancelled];
}

/// Thrown by a method to give the server a precise failure.
class EssentialsError implements Exception {
  const EssentialsError(this.message, [this.code = EssentialsErrorCodes.failed]);

  final String message;

  /// One of [EssentialsErrorCodes].
  final String code;

  @override
  String toString() => 'EssentialsError($code): $message';
}

/// The code of a failure: the one of an [EssentialsError], the meaning of a Dart error (an argument, a time out, something not
/// implemented), `failed` otherwise.
String essentialsErrorCode(Object error) => switch (error) {
      EssentialsError(:final code) when EssentialsErrorCodes.all.contains(code) => code,
      ArgumentError() || TypeError() || FormatException() => EssentialsErrorCodes.invalidArgument,
      TimeoutException() => EssentialsErrorCodes.timeout,
      UnsupportedError() || UnimplementedError() => EssentialsErrorCodes.notSupported,
      _ => EssentialsErrorCodes.failed,
    };

String _messageOf(Object error) => switch (error) {
      EssentialsError(:final message) => message,
      ArgumentError(:final message) when message != null => '$message',
      UnsupportedError(:final message) when message != null => message,
      _ => '$error',
    };

/// Runs the device APIs of this client for the server: it announces them after each `Hello`, answers the calls (`EssentialsInvoke`)
/// and starts or stops the events (`EssentialsSubscribe`). The client makes one when it is given `essentials`.
class EssentialsBridge {
  EssentialsBridge(this.services, this._call);

  /// The device APIs of this client, by service name (`preferences`, `connectivity`).
  final Map<String, EssentialsService> services;
  final Future<Object?> Function(String method, List<Object?> args) _call;
  final Map<String, void Function()> _listeners = {};

  /// The services and their state, as `EssentialsReady` sends them. The states are read together (some are asynchronous: the battery).
  Future<Map<String, Object?>> announcement() async {
    final capabilities = [...services.keys];
    final state = <String, Object?>{};
    Future<void> read(String name, FutureOr<Object?> Function() reader) async {
      try {
        final value = await reader();
        if (value != null) state[name] = value;
      } catch (_) {
        // A service that cannot read its state is announced without one: its synchronous members answer with defaults.
      }
    }

    await Future.wait([
      for (final name in capabilities)
        if (services[name]!.state case final reader?) read(name, reader),
    ]);
    return {'version': essentialsProtocol, 'capabilities': capabilities, 'state': state};
  }

  /// Tells the server what this client supports. Called after each `Hello`: the events that ran are stopped first, the server asks
  /// again for those it still wants (it may have stopped some while the connection was gone).
  Future<Map<String, Object?>> ready() async {
    stopAll();
    return asMap(await _call('EssentialsReady', [await announcement()]));
  }

  /// A call of the server: runs the method and answers.
  Future<void> invoke(Map<String, Object?> message) async {
    final id = message['id'];
    Map<String, Object?> answer;
    try {
      final service = message['service'], name = message['method'];
      final method = service is String && name is String ? services[service]?.methods[name] : null;
      if (method == null) throw EssentialsError('This client does not implement $service.$name.', EssentialsErrorCodes.notSupported);
      final args = message['args'];
      final result = await method(args is List ? [...args] : const []);
      answer = {'id': id, 'success': true, 'result': result};
    } catch (error) {
      answer = {'id': id, 'success': false, 'error': _messageOf(error), 'code': essentialsErrorCode(error)};
    }
    try {
      await _call('EssentialsResult', [answer]);
    } catch (_) {
      // The connection dropped: a replayable call comes again once the session is resumed, the others end on the server.
    }
  }

  /// The server starts or stops an event.
  void subscribe(Map<String, Object?> message) {
    final service = message['service'], event = message['event'];
    if (service is! String || event is! String) return;
    final key = '$service\u0000$event';
    if (message['active'] != true) {
      _stop(key);
      return;
    }
    if (_listeners.containsKey(key)) return;
    final start = services[service]?.events[event];
    if (start == null) return;
    _listeners[key] = start((payload) {
      _call('EssentialsEvent', [
        {'service': service, 'event': event, 'payload': payload},
      ]).catchError((Object _) => null); // Not connected: the server reads the state again when the session is resumed.
    });
  }

  /// Stops every event (the client is done with the connection).
  void stopAll() {
    for (final key in [..._listeners.keys]) {
      _stop(key);
    }
  }

  void _stop(String key) {
    final stop = _listeners.remove(key);
    try {
      stop?.call();
    } catch (_) {
      // Stopping a listener of the device does not fail the client.
    }
  }
}
