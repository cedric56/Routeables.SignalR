import 'package:flutter/material.dart';
import 'package:routeables_client/routeables_client.dart' as rt;

import '../widgets.dart';

/// The Essentials page: the server calls the device APIs of this app (lib/essentials.dart) with the .NET MAUI contracts, and shows what
/// came back. The page only draws the list: every test runs on the server.
class EssentialsPage extends StatelessWidget {
  const EssentialsPage({super.key, required this.page, required this.target});

  final rt.Page page;
  final String target;

  @override
  Widget build(BuildContext context) {
    final s = page.state;
    final groups = <String, List<Map<String, Object?>>>{};
    for (final check in objects(s['checks'])) {
      (groups[text(check['group'])] ??= []).add(check);
    }
    final capabilities = texts(s['capabilities']);

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Status(text(s['summary'])),
        if (capabilities.isNotEmpty)
          Padding(
            padding: const EdgeInsets.only(bottom: 12),
            child: Wrap(spacing: 6, runSpacing: 6, children: [for (final capability in capabilities) Chip(label: Text(capability))]),
          ),
        for (final MapEntry(key: group, value: checks) in groups.entries)
          Section(title: group, children: [for (final check in checks) _CheckTile(check: check, target: target)]),
      ],
    );
  }
}

class _CheckTile extends StatelessWidget {
  const _CheckTile({required this.check, required this.target});

  final Map<String, Object?> check;
  final String target;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    final status = text(check['status']);
    final supported = check['supported'] == true;
    final (background, foreground) = switch (status) {
      'ok' => (scheme.primary, scheme.onPrimary),
      'failed' || 'denied' => (scheme.error, scheme.onError),
      'running' => (scheme.tertiary, scheme.onTertiary),
      _ => (scheme.surfaceContainerHighest, scheme.onSurfaceVariant),
    };
    final result = text(check['result']);

    return Opacity(
      opacity: supported ? 1 : .6,
      child: Padding(
        padding: const EdgeInsets.symmetric(vertical: 8),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(children: [
              Expanded(
                child: Wrap(spacing: 8, crossAxisAlignment: WrapCrossAlignment.center, children: [
                  Text(text(check['name']), style: const TextStyle(fontWeight: FontWeight.w600)),
                  DecoratedBox(
                    decoration: BoxDecoration(color: background, borderRadius: BorderRadius.circular(99)),
                    child: Padding(
                      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 2),
                      child: Text(status.toUpperCase(), style: TextStyle(color: foreground, fontSize: 11, fontWeight: FontWeight.w600)),
                    ),
                  ),
                ]),
              ),
              CommandButton(target: target, command: check['runCommand'], label: text(check['label']), enabled: supported && status != 'running'),
            ]),
            Text(text(check['description']), style: Theme.of(context).textTheme.bodySmall),
            if (result.isNotEmpty) SelectableText(result, style: const TextStyle(fontFamily: 'monospace', fontSize: 12)),
          ],
        ),
      ),
    );
  }
}
