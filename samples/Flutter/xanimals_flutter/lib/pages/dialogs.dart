import 'package:flutter/material.dart';
import 'package:routeables_client/routeables_client.dart' as rt;

import '../widgets.dart';

const _groups = <(String, List<(String, String)>)>[
  ('Messages', [
    ('okCommand', 'Ok'),
    ('errorCommand', 'Error'),
    ('yesNoCommand', 'Yes / No'),
    ('okCancelCommand', 'Ok / Cancel'),
    ('okAbortCommand', 'Ok / Abort'),
    ('yesNoCancelCommand', 'Yes / No / Cancel'),
    ('yesNoAbortCommand', 'Yes / No / Abort'),
  ]),
  ('Rich dialogs', [
    ('contentDialogCommand', 'Content dialog'),
    ('taskDialogCommand', 'Task dialog'),
    ('progressDialogCommand', 'Task dialog with progress'),
    ('modalCommand', 'Show modal'),
  ]),
  ('Notifications', [
    ('informationCommand', 'Information'),
    ('successCommand', 'Success'),
    ('warningCommand', 'Warning'),
    ('notificationErrorCommand', 'Error'),
    ('systemNotificationCommand', 'System'),
  ]),
];

/// The buttons that open the dialogs and the notifications. A dialog goes to the app (see interactions.dart) and the view model waits
/// for the answer on the server, like it waits for a window on a desktop.
class DialogsPage extends StatelessWidget {
  const DialogsPage({super.key, required this.page, required this.target});

  final rt.Page page;
  final String target;

  @override
  Widget build(BuildContext context) => Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          for (final (title, actions) in _groups)
            Section(title: title, children: [
              ButtonRow(children: [
                for (final (command, label) in actions) CommandButton(target: target, command: page.state[command], label: label),
              ]),
            ]),
          if (page.state['hasResult'] == true) Status(text(page.state['result'])),
        ],
      );
}
