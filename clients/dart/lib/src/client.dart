import 'dart:async';

import 'patch.dart';
import 'types.dart';

typedef PageListener = void Function(Page? page);
typedef FailureListener = void Function(Failure failure);
typedef ConnectionListener = void Function(bool connected);
typedef DialogListener = void Function(List<Dialog> dialogs);
typedef ToastListener = void Function(List<Toast> toasts);

/// A call of the client that failed without anybody waiting for its answer (a watch), or that did not reach the server.
class ClientError {
  const ClientError({required this.method, this.target, required this.message, this.code});

  final String method;
  final String? target;
  final String message;
  final String? code;

  @override
  String toString() => '$method: $message';
}

typedef ErrorListener = void Function(ClientError error);

/// What the recovery of a lost session knows.
class Recovery {
  const Recovery(this.client, this.lastRoute);

  final RouteablesClient client;

  /// The route the main page showed before the session was lost, or null.
  final String? lastRoute;
}

const Ack _sessionLost = Ack(success: false, error: 'The session was lost: the call may not have ended.', code: 'session-lost');

/// Holds what the server shows, and sends what the user does. It does not know Flutter's widgets: a widget subscribes and shows
/// `page.state`; a layer of the app turns the subscriptions into a `ChangeNotifier`, a stream, a `ValueNotifier`.
///
/// What the server shows is a set of targets: the page of the main router (''), the modal (r'$modal'), the regions (by their name)
/// and the view models the application registered (r'$name'). The page and the modal are always sent; the others are sent while
/// somebody watches them ([subscribeTo]).
///
/// Every target is an ordered stream: a page (a generation), then its changes (versions 1, 2, …). A change of an older page is dropped,
/// and a missing change makes the client ask the state again. After a reconnection the client resumes its session on the server: the
/// view models, their state and the dialogs that wait come back; when it cannot, [recover] runs.
class RouteablesClient {
  /// Creates the client of a transport.
  ///
  /// [recover] is what to do when the server could not resume the session after a reconnection (the view models are new, nothing is
  /// shown): by default the main router goes back to the route it showed. [recoverAutomatically] false does nothing (listen to
  /// [onSessionLost]).
  RouteablesClient(this.transport, {Future<void> Function(Recovery recovery)? recover, this.recoverAutomatically = true})
      : _recover = recover {
    transport.onConnectionChange((connected) {
      if (!connected) {
        // The pages stay on screen: the session is resumed when the connection comes back, or recovered.
        _setConnected(false);
        return;
      }
      _hello().catchError((Object error) {
        _fail('Hello', null, error);
      });
    });
  }

  final RouteableTransport transport;
  final bool recoverAutomatically;
  final Future<void> Function(Recovery recovery)? _recover;

  final Map<String, Page?> _pages = {};
  final Map<String, Set<PageListener>> _pageListeners = {};
  final Map<String, int> _watchers = {};
  final Set<FailureListener> _failureListeners = {};
  final Set<ConnectionListener> _connectionListeners = {};
  final Set<DialogListener> _dialogListeners = {};
  final Set<ToastListener> _toastListeners = {};
  final Set<ErrorListener> _errorListeners = {};
  final Set<void Function(Recovery recovery)> _sessionLostListeners = {};
  final Map<String, int> _instances = {};
  final Set<String> _resyncing = {};
  final Map<String, Completer<Ack>> _operations = {};
  final Map<String, Ack> _early = {};
  List<Dialog> _dialogs = const [];
  List<Toast> _toasts = const [];
  bool _connected = false;
  int _sequence = 0;
  String? _session;
  String? _lastRoute;

  /// The page of the main router, or null before the first one.
  Page? get page => pageOf(pageTarget);

  /// Whether the client is connected and has its session.
  bool get isConnected => _connected;

  /// What a target shows now.
  Page? pageOf(String target) => _pages[target];

  /// The dialogs that wait for an answer.
  List<Dialog> get dialogs => _dialogs;

  /// The notifications that are shown.
  List<Toast> get toasts => _toasts;

  /// Opens the connection and says hello (a session that dropped is resumed). Go to a route afterwards.
  Future<void> connect() async {
    await transport.start(ServerMessages(
      navigated: _onNavigated,
      changed: _onChanged,
      failed: (message) {
        final failure = Failure.fromJson(message);
        for (final listener in [..._failureListeners]) {
          listener(failure);
        }
      },
      dialogRequested: (message) => _onDialog(Dialog.fromJson(message)),
      notified: (message) => _onToast(Toast.fromJson(message)),
      closed: _onClosed,
      completed: _onCompleted,
    ));
    await _hello();
  }

  Future<void> disconnect() => transport.stop();

  // ---------------------------------------------------------------- what the user does

  /// Goes to a route, as an address bar would. The page arrives through the subscription; the answer comes once the route change ended.
  Future<Ack> goTo(String route) => _send('GoTo', null, [route]);

  /// Goes to a route in the router of a target: a region, by its name.
  Future<Ack> goToIn(String target, String route) => target == pageTarget ? goTo(route) : _send('GoToIn', target, [target, route]);

  /// Calls a method of the current page, by its name. The answer comes when the command ended (after a dialog it asked, for instance).
  Future<Ack> invoke(String command, [List<Object?> args = const []]) => _send('Invoke', null, [command, args]);

  /// Calls a method of what a target shows, by its name.
  Future<Ack> invokeOn(String target, String command, [List<Object?> args = const []]) =>
      _send('InvokeOn', target, [target, command, args]);

  /// Calls a command of the state (the map found in the state of a target: `{ r'$command': path, … }`).
  Future<Ack> run(String target, Object? command, [List<Object?> args = const []]) {
    if (!isCommand(command)) return Future.value(Ack.failure('There is no command here.', 'not-found'));
    return invokeOn(target, (command as Map)[r'$command'] as String, args);
  }

  /// Sets a property of the current page (a text field, a check box). The server confirms by telling the change.
  Future<Ack> set(String property, Object? value) => setOn(pageTarget, property, value);

  /// Sets a property of what a target shows.
  Future<Ack> setOn(String target, String property, Object? value) => target == pageTarget
      ? _send('Set', target, [property, value])
      : _send('SetOn', target, [target, property, value]);

  /// Answers a dialog: the index of the button the user chose. The dialog leaves the list once the server took the answer (or does not
  /// know the dialog any more); it stays when the answer did not reach the server, to be answered again.
  Future<Ack> answer(Dialog dialog, int button) async {
    final ack = await _send('Answer', null, [dialog.id, button]);
    if (ack.success || ack.code == 'not-found') _setDialogs([for (final d in _dialogs) if (d.id != dialog.id) d]);
    return ack;
  }

  /// Closes a notification, or tells that the user clicked it. It leaves the list once the server took it.
  Future<Ack> dismiss(Toast toast, {bool click = false}) async {
    final ack = await _send('Dismiss', null, [toast.id, click]);
    if (ack.success || ack.code == 'not-found') _setToasts([for (final t in _toasts) if (t.id != toast.id) t]);
    return ack;
  }

  // ---------------------------------------------------------------- what the user sees

  /// Calls [listener] now and at every change of the main page or its state. Returns the way to stop.
  void Function() subscribe(PageListener listener) => subscribeTo(pageTarget, listener);

  /// Calls [listener] now and at every change of what a target shows. A region or a registered view model is sent by the server while
  /// somebody subscribes. Returns the way to stop.
  void Function() subscribeTo(String target, PageListener listener) {
    final listeners = _pageListeners.putIfAbsent(target, () => <PageListener>{});
    // A listener is a closure: the same function given twice is two subscriptions, as two widgets would make.
    void entry(Page? page) => listener(page);
    listeners.add(entry);
    _watch(target);
    listener(pageOf(target));
    var subscribed = true;
    return () {
      if (!subscribed) return;
      subscribed = false;
      listeners.remove(entry);
      _unwatch(target);
    };
  }

  void Function() subscribeDialogs(DialogListener listener) {
    _dialogListeners.add(listener);
    listener(_dialogs);
    return () => _dialogListeners.remove(listener);
  }

  void Function() subscribeToasts(ToastListener listener) {
    _toastListeners.add(listener);
    listener(_toasts);
    return () => _toastListeners.remove(listener);
  }

  /// What failed on the server without a call waiting for it: a route change, a part of a state that could not be sent.
  void Function() onFailure(FailureListener listener) {
    _failureListeners.add(listener);
    return () => _failureListeners.remove(listener);
  }

  /// A call that did not reach the server, or a watch the server refused.
  void Function() onError(ErrorListener listener) {
    _errorListeners.add(listener);
    return () => _errorListeners.remove(listener);
  }

  /// Whether the client is connected (with its session): false when the connection drops, true when it comes back.
  void Function() onConnection(ConnectionListener listener) {
    _connectionListeners.add(listener);
    return () => _connectionListeners.remove(listener);
  }

  /// The server could not resume the session after a reconnection: the view models are new. Called before the recovery.
  void Function() onSessionLost(void Function(Recovery recovery) listener) {
    _sessionLostListeners.add(listener);
    return () => _sessionLostListeners.remove(listener);
  }

  // ---------------------------------------------------------------- session

  Future<void> _hello() async {
    final previous = _session;
    final welcome = asMap(await transport.call('Hello', [
      {'protocol': protocolVersion, 'resume': previous},
    ]));
    if (welcome['success'] != true) {
      final message = welcome['error'] as String? ?? 'The server speaks the protocol ${welcome['protocol']}, this client $protocolVersion.';
      _fail('Hello', null, message, 'protocol');
      throw StateError(message);
    }
    _session = welcome['session'] as String?;
    final resumed = welcome['resumed'] == true;

    if (previous != null && !resumed) {
      // A new session: what was shown belongs to view models that are gone.
      for (final target in [..._pages.keys]) {
        _setPage(target, null);
      }
      _instances.clear();
      _setDialogs(const []);
      _setToasts(const []);
      final waiting = [..._operations.values];
      _operations.clear();
      for (final completer in waiting) {
        completer.complete(_sessionLost);
      }
    }

    _setConnected(true);
    // A resumed session already sent what it watches; a new one watches nothing yet.
    if (!resumed) {
      for (final target in [..._watchers.keys]) {
        _watchRemote(target);
      }
    }

    if (previous != null && !resumed) {
      final recovery = Recovery(this, _lastRoute);
      for (final listener in [..._sessionLostListeners]) {
        listener(recovery);
      }
      if (recoverAutomatically) {
        final recover = _recover ?? (Recovery r) async {
          if (r.lastRoute != null) await r.client.goTo(r.lastRoute!);
        };
        await recover(recovery);
      }
    }
  }

  void _setConnected(bool connected) {
    if (_connected == connected) return;
    _connected = connected;
    for (final listener in [..._connectionListeners]) {
      listener(connected);
    }
  }

  // Sends a call and waits for its end: the answer, or Completed when the server answered that it goes on.
  Future<Ack> _send(String method, String? target, List<Object?> args) async {
    final Ack ack;
    try {
      ack = Ack.fromJson(await transport.call(method, args));
    } catch (error) {
      return _fail(method, target, error);
    }
    final operation = ack.operation;
    if (!ack.pending || operation == null) return ack;

    final early = _early.remove(operation);
    if (early != null) return early;
    final completer = Completer<Ack>();
    _operations[operation] = completer;
    return completer.future;
  }

  Ack _fail(String method, String? target, Object error, [String code = 'transport']) {
    final message = error is StateError ? error.message : error.toString();
    final clientError = ClientError(method: method, target: target, message: message, code: code);
    for (final listener in [..._errorListeners]) {
      listener(clientError);
    }
    return Ack.failure(message, code);
  }

  // ---------------------------------------------------------------- watchers

  bool _isAlwaysSent(String target) => target == pageTarget || target == modalTarget;

  void _watch(String target) {
    if (_isAlwaysSent(target)) return;
    final count = (_watchers[target] ?? 0) + 1;
    _watchers[target] = count;
    if (count == 1 && _connected) _watchRemote(target);
  }

  void _watchRemote(String target) {
    _send('Watch', target, [target]).then((ack) {
      if (!ack.success && ack.code != 'transport') {
        final error = ClientError(method: 'Watch', target: target, message: ack.error ?? 'The target cannot be watched.', code: ack.code);
        for (final listener in [..._errorListeners]) {
          listener(error);
        }
      }
    });
  }

  void _unwatch(String target) {
    if (_isAlwaysSent(target)) return;
    final count = (_watchers[target] ?? 0) - 1;
    if (count > 0) {
      _watchers[target] = count;
      return;
    }
    _watchers.remove(target);
    _setPage(target, null);
    if (_connected) _send('Unwatch', target, [target]);
  }

  void _resync(String target) {
    if (!_resyncing.add(target)) return;
    _send('Resync', target, [target]).then((ack) {
      if (!ack.success) _resyncing.remove(target);
    });
  }

  // ---------------------------------------------------------------- what the server tells

  void _onNavigated(Map<String, Object?> message) {
    final target = message['target'] as String? ?? pageTarget;
    final generation = (message['generation'] as num?)?.toInt() ?? 0;
    final instance = (message['instance'] as num?)?.toInt() ?? 0;
    final current = pageOf(target);
    if (current != null && generation < current.generation) return; // an older page

    _resyncing.remove(target);
    // The same view model sent again (a resync, a resumed session) keeps its sequence: a widget is not recreated.
    final sameInstance = current != null && _instances[target] == instance;
    _instances[target] = instance;
    final route = message['route'] as String? ?? '';
    if (target == pageTarget) _lastRoute = route;
    _setPage(
      target,
      Page(
        sequence: sameInstance ? current.sequence : ++_sequence,
        target: target,
        route: route,
        viewModel: message['viewModel'] as String? ?? '',
        state: asMap(message['state']),
        commands: [for (final command in message['commands'] as List? ?? const []) command.toString()],
        generation: generation,
        version: 0,
      ),
    );
  }

  void _onChanged(Map<String, Object?> message) {
    final target = message['target'] as String? ?? pageTarget;
    final generation = (message['generation'] as num?)?.toInt() ?? 0;
    final version = (message['version'] as num?)?.toInt() ?? 0;
    final current = pageOf(target);
    if (current == null || generation < current.generation) return; // a change of a page that is gone
    if (generation > current.generation || version > current.version + 1) {
      _resync(target); // a page or a change was lost: the whole state is asked again
      return;
    }
    if (version <= current.version) return; // already applied

    var state = current.state;
    for (final patch in message['patches'] as List? ?? const []) {
      state = applyPatch(state, Patch.fromJson(asMap(patch)));
    }
    _setPage(target, current.copyWith(state: state, version: version));
  }

  void _onClosed(Map<String, Object?> message) {
    final target = message['target'] as String? ?? pageTarget;
    final generation = (message['generation'] as num?)?.toInt();
    final current = pageOf(target);
    if (current != null && generation != null && current.generation > generation) return; // closes an older page
    _instances.remove(target);
    _setPage(target, null);
  }

  void _onCompleted(Map<String, Object?> message) {
    final operation = message['operation'] as String? ?? '';
    final ack = Ack(success: message['success'] == true, error: message['error'] as String?, code: message['code'] as String?);
    final completer = _operations.remove(operation);
    if (completer == null) {
      // Arrived before the answer that names it was read: kept for it.
      _early[operation] = ack;
      if (_early.length > 256) _early.remove(_early.keys.first);
      return;
    }
    completer.complete(ack);
  }

  void _onDialog(Dialog dialog) {
    if (_dialogs.any((d) => d.id == dialog.id)) return; // sent again by a resumed session
    _setDialogs([..._dialogs, dialog]);
  }

  void _onToast(Toast toast) {
    if (_toasts.any((t) => t.id == toast.id)) return;
    _setToasts([..._toasts, toast]);
  }

  void _setPage(String target, Page? page) {
    _pages[target] = page;
    for (final listener in [...?_pageListeners[target]]) {
      listener(page);
    }
  }

  void _setDialogs(List<Dialog> dialogs) {
    _dialogs = List.unmodifiable(dialogs);
    for (final listener in [..._dialogListeners]) {
      listener(_dialogs);
    }
  }

  void _setToasts(List<Toast> toasts) {
    _toasts = List.unmodifiable(toasts);
    for (final listener in [..._toastListeners]) {
      listener(_toasts);
    }
  }
}
