import 'package:flutter/material.dart';
import 'package:routeables_client/routeables_client.dart' as rt;

import '../widgets.dart';

class AboutPage extends StatelessWidget {
  const AboutPage({super.key, required this.page, required this.target});

  final rt.Page page;
  final String target;

  @override
  Widget build(BuildContext context) => Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text('Routeables Animals', style: Theme.of(context).textTheme.headlineMedium),
          Text('1.0', style: Theme.of(context).textTheme.bodySmall),
          const SizedBox(height: 16),
          Section(title: 'Links', children: [
            ButtonRow(children: [
              // A link of the server: the view model of this page opens the polar bears.
              CommandButton(target: target, command: page.state['polarBearsCommand'], label: 'Polar bears'),
            ]),
            const SizedBox(height: 8),
            const SelectableText('https://github.com/cedric56/Routeables'),
          ]),
        ],
      );
}
