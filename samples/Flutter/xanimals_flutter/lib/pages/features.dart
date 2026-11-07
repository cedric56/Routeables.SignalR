import 'package:flutter/material.dart';
import 'package:routeables_client/routeables_client.dart' as rt;

import '../widgets.dart';

class FeaturesPage extends StatelessWidget {
  const FeaturesPage({super.key, required this.page, required this.target});

  final rt.Page page;
  final String target;

  @override
  Widget build(BuildContext context) {
    final s = page.state;
    CommandButton button(String command, String label, {bool enabled = true}) =>
        CommandButton(target: target, command: s[command], label: label, enabled: enabled);

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Section(title: 'Guard and return', children: [
          const Text('The Members page is for members. Signed out, opening it brings you here; sign in, then open it.'),
          const SizedBox(height: 8),
          ButtonRow(children: [
            button('signInCommand', 'Sign in', enabled: s['isSignedOut'] == true),
            button('signOutCommand', 'Sign out', enabled: s['isSignedIn'] == true),
            button('openMembersCommand', 'Open Members'),
          ]),
        ]),
        Section(title: 'Link', children: [
          const Text('An address from outside, followed through the router. In a browser the address bar does the same job.'),
          const SizedBox(height: 8),
          ButtonRow(children: [button('openLinkCommand', 'Open ${text(s['link'])}')]),
        ]),
        Section(title: 'Lock', children: [
          const Text('The page publishes a lock message; the menu entries are disabled until it publishes the unlock one.'),
          const SizedBox(height: 8),
          ButtonRow(children: [
            button('lockCommand', 'Lock', enabled: s['canLock'] == true),
            button('unlockCommand', 'Unlock', enabled: s['canUnlock'] == true),
            button('lockForCommand', 'Lock for 5 seconds', enabled: s['canLock'] == true),
          ]),
          Status(text(s['lockStatus'])),
        ]),
        Section(title: 'Measures', children: [
          ButtonRow(children: [button('refreshCommand', 'Show the last route changes')]),
          const SizedBox(height: 8),
          Text(text(s['measures']), style: const TextStyle(fontFamily: 'monospace')),
        ]),
        Status(text(s['status'])),
      ],
    );
  }
}
