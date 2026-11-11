/// The name of the target that is the page of the main router.
const String pageTarget = '';

/// The name of the target that is the modal view model that is open.
const String modalTarget = r'$modal';

/// The version of the messages this client speaks; the server says whether it speaks it too (`Hello`).
const int protocolVersion = 2;

/// The state of a view model, as the server sends it: its properties by their camel case name. Values are `null`, numbers,
/// strings, booleans, lists and maps of the same kind; a command is a map `{ r'$command': path, 'canExecute': bool }`.
typedef State = Map<String, Object?>;

/// A page the server made current: what the widget that shows it needs.
class Page {
  const Page({
    required this.sequence,
    required this.target,
    required this.route,
    required this.viewModel,
    required this.state,
    required this.commands,
    required this.generation,
    required this.version,
  });

  /// Counts the pages the target showed: it changes when another page arrives, not when the state of this one changes or is sent again.
  final int sequence;

  /// What shows it: the main page (''), the modal (r'$modal'), a region (its name) or a registered view model (r'$name').
  final String target;

  /// The address of the page.
  final String route;

  /// The name of the view model type, to choose the widget.
  final String viewModel;

  /// The properties of the view model. A new map after every change; what did not change is shared.
  final State state;

  /// The methods the page may call by name (the ones marked `[RouteableCommand]` that the user may call).
  final List<String> commands;

  /// The generation of the page on the server: the changes that belong to it carry it.
  final int generation;

  /// The last change applied (0 for the state as it arrived).
  final int version;

  Page copyWith({State? state, int? version}) => Page(
        sequence: sequence,
        target: target,
        route: route,
        viewModel: viewModel,
        state: state ?? this.state,
        commands: commands,
        generation: generation,
        version: version ?? this.version,
      );
}

/// Whether [value] is a command of the state (an ICommand of the view model): give it to `run` to call it.
bool isCommand(Object? value) => value is Map && value[r'$command'] is String;

/// Whether a command of the state can run now (a missing command can: the server says no).
bool canRun(Object? command) => !(command is Map && command['canExecute'] == false);

/// A dialog the user has to answer. The answer is the index of the button.
class Dialog {
  const Dialog({
    required this.id,
    required this.kind,
    required this.title,
    this.header,
    this.body,
    this.footer,
    this.showProgress = false,
    this.buttons = const [],
  });

  factory Dialog.fromJson(Map<String, Object?> json) => Dialog(
        id: json['id'] as String,
        kind: json['kind'] as String? ?? 'message',
        title: json['title'] as String? ?? '',
        header: json['header'] as String?,
        body: json['body'] as String?,
        footer: json['footer'] as String?,
        showProgress: json['showProgress'] as bool? ?? false,
        buttons: [for (final button in json['buttons'] as List? ?? const []) button.toString()],
      );

  final String id;

  /// "message", "content" or "task".
  final String kind;
  final String title;
  final String? header;
  final String? body;
  final String? footer;
  final bool showProgress;
  final List<String> buttons;
}

/// A notification. The client closes it after [expirationSeconds] when it has one (`dismiss`).
class Toast {
  const Toast({required this.id, required this.title, required this.message, required this.type, this.expirationSeconds});

  factory Toast.fromJson(Map<String, Object?> json) => Toast(
        id: (json['id'] as num).toInt(),
        title: json['title'] as String? ?? '',
        message: json['message'] as String? ?? '',
        type: json['type'] as String? ?? 'Information',
        expirationSeconds: (json['expirationSeconds'] as num?)?.toDouble(),
      );

  final int id;
  final String title;
  final String message;

  /// "Information", "Success", "Warning" or "Error".
  final String type;
  final double? expirationSeconds;
}

/// Something failed on the server that no call waits for: a route change, a part of a state that could not be sent.
class Failure {
  const Failure({required this.route, required this.message, this.target, this.code});

  factory Failure.fromJson(Map<String, Object?> json) => Failure(
        route: json['route'] as String? ?? '',
        message: json['message'] as String? ?? '',
        target: json['target'] as String?,
        code: json['code'] as String?,
      );

  final String route;
  final String message;
  final String? target;
  final String? code;
}

/// The answer of the server to a call.
class Ack {
  const Ack({required this.success, this.error, this.code, this.pending = false, this.operation});

  factory Ack.fromJson(Object? json) {
    final map = json is Map ? json : const <String, Object?>{};
    return Ack(
      success: map['success'] == true,
      error: map['error'] as String?,
      code: map['code'] as String?,
      pending: map['pending'] == true,
      operation: map['operation'] as String?,
    );
  }

  /// A call that did not reach the server.
  factory Ack.failure(String error, String code) => Ack(success: false, error: error, code: code);

  final bool success;
  final String? error;

  /// What kind of failure: "not-found", "unauthorized", "invalid-argument", "failed", "cancelled", "limit", "disabled", "protocol",
  /// "lost" when the server forgot the end of a call the client waited for across a reconnection (the call may have run),
  /// "transport" when the call did not reach the server, "session-lost" when the session ended before the call.
  final String? code;

  /// The call went on after the server answered (a dialog, a long command): `Completed` tells its end. The client waits for it.
  final bool pending;
  final String? operation;

  @override
  String toString() => success ? 'Ack(success)' : 'Ack($code: $error)';
}

/// One change of a state: `set` replaces the value at [path]; `splice` removes [remove] items at [index] of the list at [path] and
/// inserts the items of [value].
class Patch {
  const Patch({required this.op, required this.path, this.value, this.index, this.remove});

  factory Patch.fromJson(Map<String, Object?> json) => Patch(
        op: json['op'] as String,
        path: [for (final segment in json['path'] as List) segment as Object],
        value: json['value'],
        index: (json['index'] as num?)?.toInt(),
        remove: (json['remove'] as num?)?.toInt(),
      );

  final String op;
  final List<Object> path;
  final Object? value;
  final int? index;
  final int? remove;
}

/// The server messages a transport delivers, as the JSON maps SignalR decoded.
class ServerMessages {
  const ServerMessages({
    required this.navigated,
    required this.changed,
    required this.failed,
    required this.dialogRequested,
    required this.notified,
    required this.closed,
    required this.completed,
    this.essentialsInvoke,
    this.essentialsSubscribe,
  });

  final void Function(Map<String, Object?> message) navigated;
  final void Function(Map<String, Object?> message) changed;
  final void Function(Map<String, Object?> message) failed;
  final void Function(Map<String, Object?> message) dialogRequested;
  final void Function(Map<String, Object?> message) notified;
  final void Function(Map<String, Object?> message) closed;
  final void Function(Map<String, Object?> message) completed;

  /// Essentials: a call of a device API (only when the client has `essentials`); answered with `EssentialsResult`.
  final void Function(Map<String, Object?> message)? essentialsInvoke;

  /// Essentials: an event to start or stop.
  final void Function(Map<String, Object?> message)? essentialsSubscribe;
}

/// What the client needs from a connection. SignalR is the first one (see signalr_transport.dart); another can replace it.
abstract class RouteableTransport {
  /// Opens the connection and delivers the server messages to [handlers].
  Future<void> start(ServerMessages handlers);

  /// Closes the connection.
  Future<void> stop();

  /// Calls a method of the server and returns what it answered (a JSON map); throws when the call does not reach it.
  Future<Object?> call(String method, List<Object?> args);

  /// Called with false when the connection drops (or closes), with true when it comes back.
  void onConnectionChange(void Function(bool connected) handler);
}

/// Reads a JSON object of a message as a map of strings.
Map<String, Object?> asMap(Object? value) => value is Map ? value.map((key, item) => MapEntry(key.toString(), item)) : <String, Object?>{};
