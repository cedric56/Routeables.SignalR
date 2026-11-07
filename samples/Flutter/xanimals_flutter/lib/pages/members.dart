import 'package:flutter/material.dart';
import 'package:routeables_client/routeables_client.dart' as rt;

import '../widgets.dart';

class MembersPage extends StatelessWidget {
  const MembersPage({super.key, required this.page, required this.target});

  final rt.Page page;
  final String target;

  @override
  Widget build(BuildContext context) => Section(children: [
        Text(text(page.state['welcome'])),
        const SizedBox(height: 12),
        ButtonRow(children: [CommandButton(target: target, command: page.state['signOutCommand'], label: 'Sign out')]),
      ]);
}
