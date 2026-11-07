import 'package:flutter/material.dart';
import 'package:routeables_client/routeables_client.dart' as rt;

import 'app_state.dart';

/// A value of a state as a text ('' for null).
String text(Object? value) => value?.toString() ?? '';

/// A value of a state as a list of maps (the items of a list of objects).
List<Map<String, Object?>> objects(Object? value) => [for (final item in value as List? ?? const []) rt.asMap(item)];

/// A value of a state as a list of texts.
List<String> texts(Object? value) => [for (final item in value as List? ?? const []) item.toString()];

/// A group of a page: a title and what it holds, as the `about-group` sections of the web samples.
class Section extends StatelessWidget {
  const Section({super.key, this.title, required this.children});

  final String? title;
  final List<Widget> children;

  @override
  Widget build(BuildContext context) => Card(
        margin: const EdgeInsets.only(bottom: 12),
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              if (title != null) Padding(padding: const EdgeInsets.only(bottom: 8), child: Text(title!, style: Theme.of(context).textTheme.titleMedium)),
              ...children,
            ],
          ),
        ),
      );
}

/// A row of buttons that wraps.
class ButtonRow extends StatelessWidget {
  const ButtonRow({super.key, required this.children});

  final List<Widget> children;

  @override
  Widget build(BuildContext context) => Wrap(spacing: 8, runSpacing: 8, children: children);
}

/// A button that runs a command of the state of a target. It is disabled when the command cannot run, or when [enabled] is false.
class CommandButton extends StatelessWidget {
  const CommandButton({super.key, required this.target, required this.command, required this.label, this.enabled = true});

  final String target;
  final Object? command;
  final String label;
  final bool enabled;

  @override
  Widget build(BuildContext context) => FilledButton.tonal(
        onPressed: enabled && command != null && rt.canRun(command) ? () => app.run(target, command) : null,
        child: Text(label),
      );
}

/// A line that tells what happened, as the `about-result` of the web samples.
class Status extends StatelessWidget {
  const Status(this.value, {super.key, this.refused = false});

  final String value;
  final bool refused;

  @override
  Widget build(BuildContext context) => value.isEmpty
      ? const SizedBox.shrink()
      : Padding(
          padding: const EdgeInsets.symmetric(vertical: 8),
          child: Text(value, style: TextStyle(color: refused ? Theme.of(context).colorScheme.error : null, fontStyle: FontStyle.italic)),
        );
}

/// A photo of the server (a web address), or an icon when there is none or it cannot be loaded.
Widget photo(Object? url, {required double width, required double height, BorderRadius radius = BorderRadius.zero}) {
  final placeholder = SizedBox(width: width, height: height, child: const Icon(Icons.pets));
  final address = text(url);
  if (address.isEmpty) return placeholder;
  return ClipRRect(
    borderRadius: radius,
    child: Image.network(address, width: width, height: height, fit: BoxFit.cover, errorBuilder: (context, error, stack) => placeholder),
  );
}
