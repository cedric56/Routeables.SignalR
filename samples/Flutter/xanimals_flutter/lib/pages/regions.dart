import 'package:flutter/material.dart';
import 'package:routeables_client/routeables_client.dart' as rt;

import '../app_state.dart';
import '../region.dart';
import '../widgets.dart';

/// An application of its own: four zones, four routers. Each zone shows whatever the router of its region displays.
class RegionsPage extends StatelessWidget {
  const RegionsPage({super.key, required this.page, required this.target});

  final rt.Page page;
  final String target;

  @override
  Widget build(BuildContext context) {
    final s = page.state;
    final collapsed = s['isLeftCollapsed'] == true;
    final border = BoxDecoration(border: Border.all(color: Theme.of(context).colorScheme.outlineVariant), borderRadius: BorderRadius.circular(10));
    Widget zone(String name) => Container(decoration: border, padding: const EdgeInsets.all(12), child: Region(name: name));

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        zone('shell-header'),
        const SizedBox(height: 8),
        Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            AnimatedContainer(
              duration: const Duration(milliseconds: 220),
              width: collapsed ? 64 : 240,
              decoration: border,
              padding: const EdgeInsets.all(8),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  IconButton(
                    tooltip: collapsed ? 'Show the panel' : 'Reduce the panel',
                    icon: Icon(collapsed ? Icons.chevron_right : Icons.chevron_left),
                    onPressed: () => app.run(target, s['toggleLeftCommand']),
                  ),
                  if (!collapsed) ...[
                    const Region(name: 'shell-left'),
                    // One navigation made of segments: the logo in the content, the author in the footer, the copyright in the left panel.
                    TextButton(onPressed: () => app.run(target, s['creditsCommand']), child: Text(text(s['creditsText']))),
                  ],
                ],
              ),
            ),
            const SizedBox(width: 8),
            Expanded(child: zone('shell-content')),
          ],
        ),
        const SizedBox(height: 8),
        zone('shell-footer'),
      ],
    );
  }
}
