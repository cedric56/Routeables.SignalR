import 'package:flutter/foundation.dart';

/// The address of the hub of Routeables.SignalR. `--dart-define=ROUTEABLES_URL=http://192.168.1.10:5090/routeables` chooses it;
/// otherwise the sample server (samples/Server) on this machine: `localhost`, or `10.0.2.2` from the Android emulator (its name for the
/// machine that runs it). A phone needs the address of the machine on the network, and a server that listens on it
/// (`--urls http://0.0.0.0:5090`).
String serverUrl() {
  const configured = String.fromEnvironment('ROUTEABLES_URL');
  if (configured.isNotEmpty) return configured;
  final host = !kIsWeb && defaultTargetPlatform == TargetPlatform.android ? '10.0.2.2' : 'localhost';
  return 'http://$host:5090/routeables';
}
