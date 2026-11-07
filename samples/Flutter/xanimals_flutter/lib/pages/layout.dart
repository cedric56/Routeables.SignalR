import 'package:flutter/material.dart';
import 'package:routeables_client/routeables_client.dart' as rt;

import '../app_state.dart';
import '../widgets.dart';

/// The header and the footer of the Regions page.
class LayoutBar extends StatelessWidget {
  const LayoutBar({super.key, required this.page});

  final rt.Page page;

  @override
  Widget build(BuildContext context) {
    final s = page.state;
    final strong = s['isStrong'] == true;
    return Row(children: [
      Text(text(s['glyph']), style: const TextStyle(fontSize: 24)),
      const SizedBox(width: 10),
      Expanded(
        child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Text(text(s['title']), style: TextStyle(fontWeight: strong ? FontWeight.w800 : FontWeight.w600)),
          Text(text(s['detail']), style: Theme.of(context).textTheme.bodySmall),
        ]),
      ),
    ]);
  }
}

/// The logo the content region of the Regions page shows.
class LayoutLogo extends StatelessWidget {
  const LayoutLogo({super.key, required this.page});

  final rt.Page page;

  @override
  Widget build(BuildContext context) => Center(
        child: Padding(
          padding: const EdgeInsets.all(24),
          child: Text(text(page.state['name']), style: Theme.of(context).textTheme.displaySmall?.copyWith(color: const Color(0xFF5B35E0))),
        ),
      );
}

/// The menu of the left panel. It is in a region of its own: its commands are called on that region.
class LayoutMenu extends StatelessWidget {
  const LayoutMenu({super.key, required this.page, required this.target});

  final rt.Page page;
  final String target;

  @override
  Widget build(BuildContext context) {
    final s = page.state;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        ButtonRow(children: [
          CommandButton(target: target, command: s['backCommand'], label: '◀ Back', enabled: s['canGoBack'] == true),
          CommandButton(target: target, command: s['forwardCommand'], label: 'Forward ▶', enabled: s['canGoForward'] == true),
        ]),
        const SizedBox(height: 8),
        for (final item in objects(s['items']))
          ListTile(
            dense: true,
            leading: Text(text(item['glyph'])),
            title: Text(text(item['name'])),
            selected: item['isCurrent'] == true,
            onTap: () => app.run(target, item['select']),
          ),
      ],
    );
  }
}
