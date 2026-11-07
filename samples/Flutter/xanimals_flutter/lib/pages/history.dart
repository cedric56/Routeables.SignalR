import 'package:flutter/material.dart';
import 'package:routeables_client/routeables_client.dart' as rt;

import '../app_state.dart';
import '../region.dart';
import '../widgets.dart';

/// The history of a region, as cards, and the frame that shows the page the region displays.
class HistoryPage extends StatelessWidget {
  const HistoryPage({super.key, required this.page, required this.target});

  final rt.Page page;
  final String target;

  @override
  Widget build(BuildContext context) {
    final s = page.state;
    final scheme = Theme.of(context).colorScheme;
    final transitions = texts(s['transitions']);
    final transition = text(s['transition']);

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Section(children: [
          SingleChildScrollView(
            scrollDirection: Axis.horizontal,
            child: Row(children: [
              for (final entry in objects(s['entries']))
                Container(
                  width: 120,
                  margin: const EdgeInsets.only(right: 8),
                  padding: const EdgeInsets.all(8),
                  decoration: BoxDecoration(
                    borderRadius: BorderRadius.circular(10),
                    border: Border.all(color: entry['isCurrent'] == true ? scheme.primary : scheme.outlineVariant, width: entry['isCurrent'] == true ? 2 : 1),
                    color: entry['isAhead'] == true ? scheme.surfaceContainerHighest : null,
                  ),
                  child: Column(children: [
                    Text('${text(entry['number'])} · ${text(entry['badge'])}', style: Theme.of(context).textTheme.labelSmall),
                    Text(text(entry['glyph']), style: const TextStyle(fontSize: 24)),
                    Text(text(entry['name']), maxLines: 1, overflow: TextOverflow.ellipsis),
                    Text(text(entry['caption']), maxLines: 1, overflow: TextOverflow.ellipsis, style: Theme.of(context).textTheme.bodySmall),
                  ]),
                ),
            ]),
          ),
        ]),
        Section(children: [
          // The zone of the "history" region: nothing assigns the page, it shows what the router of the region displays.
          IgnorePointer(child: Region(name: 'history', transition: transition.isEmpty ? 'None' : transition)),
        ]),
        Section(children: [
          ButtonRow(children: [
            CommandButton(target: target, command: s['pushCommand'], label: '＋ Push'),
            CommandButton(target: target, command: s['backCommand'], label: '◀ Back', enabled: s['canGoBack'] == true),
            CommandButton(target: target, command: s['forwardCommand'], label: 'Forward ▶', enabled: s['canGoForward'] == true),
            CommandButton(target: target, command: s['popCommand'], label: '↩ Pop', enabled: s['canGoBack'] == true),
          ]),
        ]),
        Section(title: 'Transition', children: [
          DropdownButton<String>(
            value: transitions.contains(transition) ? transition : null,
            items: [for (final name in transitions) DropdownMenuItem(value: name, child: Text(name))],
            onChanged: (value) => app.set(target, 'transition', value),
          ),
        ]),
        Section(title: 'CanLeaveAsync', children: [
          OutlinedButton(
            onPressed: () => app.set(target, 'blockLeaving', s['blockLeaving'] != true),
            child: Text('${text(s['guardGlyph'])} ${text(s['guardText'])}'),
          ),
        ]),
        Status('${s['isRefused'] == true ? '🚫 ' : ''}${text(s['status'])}', refused: s['isRefused'] == true),
      ],
    );
  }
}
