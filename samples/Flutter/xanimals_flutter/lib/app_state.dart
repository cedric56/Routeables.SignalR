import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:routeables_client/routeables_client.dart' as rt;

import 'server.dart';

/// The entries of the menu, as the server sends them (`$shell`).
class ShellEntry {
  ShellEntry(Map<String, Object?> json)
      : name = '${json['name'] ?? ''}',
        route = '${json['route'] ?? ''}',
        glyph = '${json['glyph'] ?? ''}',
        description = '${json['description'] ?? ''}',
        isLocked = json['isLocked'] == true;

  final String name;
  final String route;
  final String glyph;
  final String description;
  final bool isLocked;
}

/// The view models of the server, as one `ChangeNotifier` the widgets listen to. The server decides what is shown: this class shows
/// what it sends and passes on what the user does. It is the Flutter counterpart of `routeables.ts` in the web samples.
///
/// What the server shows is a set of targets (see routeables_client): the page of the main router, the modal, the regions, and the view
/// models the server registered (`$shell` is the menu, `$pets` the cards of the Adopt page).
class AppState extends ChangeNotifier {
  AppState(String url) : client = rt.RouteablesClient(rt.SignalRTransport(url)) {
    client.subscribe((page) => _update(() => this.page = page));
    client.subscribeTo(rt.modalTarget, (modal) => _update(() => this.modal = modal));
    client.subscribeTo(r'$shell', (shell) => _update(() => this.shell = shell));
    client.subscribeDialogs((dialogs) => _update(() => this.dialogs = dialogs));
    client.subscribeToasts((toasts) {
      _update(() => this.toasts = toasts);
      // A notification closes by itself after its time.
      for (final toast in toasts) {
        final seconds = toast.expirationSeconds;
        if (seconds == null || !_timers.add(toast.id)) continue;
        Timer(Duration(milliseconds: (seconds * 1000).round()), () => dismiss(toast, false));
      }
    });
    client.onFailure((failure) => _update(() => error = failure.message));
    client.onError((failure) => _update(() => error = failure.message));
    // After a reconnection the client resumes its session on the server: the pages, their state and the dialogs come back. When the
    // session is gone (the server restarted), the client opens the last page again by itself.
    client.onConnection((connected) => _update(() => this.connected = connected));
  }

  final rt.RouteablesClient client;
  final Set<int> _timers = {};

  /// The page the server made current; null before the first one (it stays while the connection is down: the session is resumed).
  rt.Page? page;

  /// The view model of the modal that is open (the note), or null.
  rt.Page? modal;

  /// The menu and the theme (target `$shell`).
  rt.Page? shell;
  List<rt.Dialog> dialogs = const [];
  List<rt.Toast> toasts = const [];
  bool connected = false;
  String? error;
  Future<void>? _starting;

  void _update(void Function() change) {
    change();
    notifyListeners();
  }

  /// The menu: the server sends it and says which entries a lock disables.
  List<ShellEntry> get entries => [for (final entry in shell?.state['entries'] as List? ?? const []) ShellEntry(rt.asMap(entry))];

  /// The theme the view models asked for (IRouteableRequestedThemeService): Default, Light or Dark.
  String get theme => '${shell?.state['theme'] ?? 'Default'}';

  /// Connects, then opens the first page. Once.
  Future<void> start() => _starting ??= () async {
        try {
          await client.connect();
          _update(() => connected = true);
          await goTo('/cats');
        } catch (_) {
          _update(() => error = 'The server is not reachable at ${serverUrl()}.');
        }
      }();

  Future<void> goTo(String route) async {
    _update(() => error = null);
    final ack = await client.goTo(route);
    if (!ack.success) _update(() => error = ack.error ?? 'This page does not exist.');
  }

  /// Goes to a route in the router of a region.
  Future<void> goToIn(String target, String route) async {
    final ack = await client.goToIn(target, route);
    if (!ack.success) _update(() => error = ack.error ?? 'This page does not exist.');
  }

  /// Calls a command of the state (the `...Command` properties of a view model) of what a target shows.
  Future<void> run(String target, Object? command) async {
    if (command == null) return;
    final ack = await client.run(target, command);
    if (!ack.success) _update(() => error = ack.error ?? 'The command failed.');
  }

  /// Sets a property of what a target shows (a text field, a switch); the server confirms with the change.
  Future<void> set(String target, String property, Object? value) => client.setOn(target, property, value);

  /// Answers a dialog: the index of the button the user chose.
  Future<void> answer(rt.Dialog dialog, int button) => client.answer(dialog, button);

  /// Closes a notification, or tells that the user clicked it.
  Future<void> dismiss(rt.Toast toast, bool click) {
    _timers.remove(toast.id);
    return client.dismiss(toast, click: click);
  }
}

/// The application, for every widget.
final app = AppState(serverUrl());
