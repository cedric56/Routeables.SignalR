import 'package:flutter/material.dart';

import '../app_state.dart';
import '../region.dart';

const _tabs = [('Cats', 'cats'), ('Dogs', 'dogs'), ('Monkeys', 'monkeys')];

/// The items of a tab control are routes: each tab shows its route in the region of the page, and the region draws it.
class TabsPage extends StatefulWidget {
  const TabsPage({super.key});

  @override
  State<TabsPage> createState() => _TabsPageState();
}

class _TabsPageState extends State<TabsPage> {
  String _selected = '';

  void _select(String route) {
    setState(() => _selected = route);
    app.goToIn('tabs-demo', route);
  }

  // A tab control selects its first item when it appears.
  @override
  void initState() {
    super.initState();
    _selected = _tabs.first.$2;
    app.goToIn('tabs-demo', _selected);
  }

  @override
  Widget build(BuildContext context) => Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          SegmentedButton<String>(
            segments: [for (final (header, route) in _tabs) ButtonSegment(value: route, label: Text(header))],
            selected: {_selected},
            onSelectionChanged: (selection) => _select(selection.first),
          ),
          const SizedBox(height: 12),
          const Card(child: Padding(padding: EdgeInsets.all(12), child: Region(name: 'tabs-demo'))),
        ],
      );
}
