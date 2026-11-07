import 'package:flutter/material.dart';
import 'package:routeables_client/routeables_client.dart' as rt;

import '../region.dart';
import '../widgets.dart';

/// One button per animal: adopting it routes its card into the region "adopt". The cards are the history of that region (`$pets`).
class AdoptPage extends StatefulWidget {
  const AdoptPage({super.key, required this.page, required this.target});

  final rt.Page page;
  final String target;

  @override
  State<AdoptPage> createState() => _AdoptPageState();
}

class _AdoptPageState extends State<AdoptPage> with FollowsTarget<AdoptPage> {
  @override
  String get target => r'$pets';

  @override
  Widget build(BuildContext context) {
    final s = widget.page.state;
    final pets = objects(followed?.state['pets']);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Section(children: [
          ButtonRow(children: [
            for (final choice in objects(s['choices']))
              CommandButton(target: widget.target, command: choice['adoptCommand'], label: text(choice['title'])),
          ]),
        ]),
        Section(children: [
          Status(text(s['status'])),
          ButtonRow(children: [
            CommandButton(target: widget.target, command: s['releaseCommand'], label: 'Release the last'),
            CommandButton(target: widget.target, command: s['releaseAllCommand'], label: 'Release all'),
          ]),
        ]),
        Wrap(spacing: 12, runSpacing: 12, children: [
          for (final pet in pets)
            Card(
              child: SizedBox(
                width: 170,
                child: Column(children: [
                  photo(rt.asMap(pet['animal'])['photoUrl'], width: 170, height: 190, radius: const BorderRadius.vertical(top: Radius.circular(12))),
                  Padding(
                    padding: const EdgeInsets.all(8),
                    child: Column(children: [
                      Text(text(pet['name']), style: const TextStyle(fontWeight: FontWeight.bold)),
                      Text(text(pet['location']), style: Theme.of(context).textTheme.bodySmall),
                      Text(text(pet['glyph']), style: const TextStyle(fontSize: 22)),
                    ]),
                  ),
                ]),
              ),
            ),
        ]),
      ],
    );
  }
}
