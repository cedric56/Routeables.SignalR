/// The client of Routeables.SignalR: it holds what the server shows and sends what the user does. It speaks the protocol 2 of
/// the server (the same as `@routeables/client`): every target is an ordered stream, changes are patches, a long call ends with
/// `Completed`, and a session is resumed after a reconnection.
library;

export 'src/client.dart';
export 'src/patch.dart' show applyPatch;
export 'src/signalr_transport.dart';
export 'src/types.dart';
