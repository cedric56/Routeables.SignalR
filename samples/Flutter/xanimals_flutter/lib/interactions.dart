import 'package:flutter/material.dart';
import 'package:routeables_client/routeables_client.dart' as rt;

import 'app_state.dart';
import 'widgets.dart';

/// What the services of the server put on the screen: the notifications, the dialog the user has to answer, and the modal (a view model of
/// the server, drawn here). The view model that asked waits on the server for the answer. It is drawn above the page, in its Stack.
class Interactions extends StatelessWidget {
  const Interactions({super.key});

  @override
  Widget build(BuildContext context) => ListenableBuilder(
        listenable: app,
        builder: (context, _) {
          final modal = app.modal;
          final dialog = app.dialogs.firstOrNull;
          return Positioned.fill(
            child: Stack(children: [
              if (modal != null)
                _Backdrop(child: NoteModal(key: ValueKey(modal.sequence), page: modal))
              else if (dialog != null)
                _Backdrop(child: _ServerDialog(dialog: dialog)),
              if (app.toasts.isNotEmpty)
                Positioned(
                  right: 16,
                  bottom: 16,
                  child: SizedBox(
                    width: 340,
                    child: Column(mainAxisSize: MainAxisSize.min, children: [for (final toast in app.toasts) _ToastCard(toast: toast)]),
                  ),
                ),
            ]),
          );
        },
      );
}

class _Backdrop extends StatelessWidget {
  const _Backdrop({required this.child});

  final Widget child;

  @override
  Widget build(BuildContext context) => Positioned.fill(
        child: ColoredBox(
          color: Colors.black54,
          child: Center(
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: 460),
              child: Card(child: Padding(padding: const EdgeInsets.all(20), child: child)),
            ),
          ),
        ),
      );
}

class _ServerDialog extends StatelessWidget {
  const _ServerDialog({required this.dialog});

  final rt.Dialog dialog;

  @override
  Widget build(BuildContext context) => Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(dialog.title, style: Theme.of(context).textTheme.titleLarge),
          if (dialog.header != null) Padding(padding: const EdgeInsets.only(top: 8), child: Text(dialog.header!, style: const TextStyle(fontWeight: FontWeight.bold))),
          if (dialog.body != null) Padding(padding: const EdgeInsets.only(top: 8), child: Text(dialog.body!)),
          if (dialog.showProgress) const Padding(padding: EdgeInsets.only(top: 12), child: LinearProgressIndicator()),
          const SizedBox(height: 16),
          ButtonRow(children: [
            for (var index = 0; index < dialog.buttons.length; index++)
              FilledButton(onPressed: () => app.answer(dialog, index), child: Text(dialog.buttons[index])),
          ]),
          if (dialog.footer != null) Padding(padding: const EdgeInsets.only(top: 12), child: Text(dialog.footer!, style: Theme.of(context).textTheme.bodySmall)),
        ],
      );
}

/// The note modal: a view model of the server, shown as the page of the target `$modal`, edited and closed through it.
class NoteModal extends StatefulWidget {
  const NoteModal({super.key, required this.page});

  final rt.Page page;

  @override
  State<NoteModal> createState() => _NoteModalState();
}

class _NoteModalState extends State<NoteModal> {
  // The field keeps what is typed: the server answers each key after the next one may already be typed. A new modal is a new field (key).
  late final TextEditingController _note = TextEditingController(text: text(widget.page.state['note']));

  @override
  void dispose() {
    _note.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final state = widget.page.state;
    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(text(state['title']), style: Theme.of(context).textTheme.titleLarge),
        const SizedBox(height: 12),
        TextField(
          controller: _note,
          autofocus: true,
          decoration: const InputDecoration(labelText: 'Note', hintText: 'Write a note'),
          onChanged: (value) => app.set(rt.modalTarget, 'note', value),
        ),
        const SizedBox(height: 16),
        ButtonRow(children: [
          for (final button in objects(state['commands']))
            CommandButton(target: rt.modalTarget, command: button['command'], label: text(button['name'])),
        ]),
      ],
    );
  }
}

class _ToastCard extends StatelessWidget {
  const _ToastCard({required this.toast});

  final rt.Toast toast;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    final color = switch (toast.type) {
      'Success' => Colors.green.shade700,
      'Warning' => Colors.orange.shade800,
      'Error' => scheme.error,
      _ => scheme.primary,
    };
    return Card(
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(12), side: BorderSide(color: color, width: 2)),
      child: ListTile(
        title: Text(toast.title, style: TextStyle(color: color, fontWeight: FontWeight.bold)),
        subtitle: Text(toast.message),
        onTap: () => app.dismiss(toast, true),
        trailing: IconButton(tooltip: 'Close', icon: const Icon(Icons.close), onPressed: () => app.dismiss(toast, false)),
      ),
    );
  }
}
