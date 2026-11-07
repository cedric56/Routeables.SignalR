import 'package:flutter/material.dart';
import 'package:routeables_client/routeables_client.dart' as rt;

import '../app_state.dart';
import '../widgets.dart';

/// The first segment of the address of a page: /cats/Abyssinian is the page of the cats.
String _kind(rt.Page page) => page.route.split('/').where((segment) => segment.isNotEmpty).firstOrNull ?? '';

/// The list of a species. A list that a region shows (hostsDetail is false) does not leave the page: no search, no link to a detail.
class AnimalListPage extends StatefulWidget {
  const AnimalListPage({super.key, required this.page, required this.target});

  final rt.Page page;
  final String target;

  @override
  State<AnimalListPage> createState() => _AnimalListPageState();
}

class _AnimalListPageState extends State<AnimalListPage> {
  // The field keeps what is typed: the server answers each key after the next one may already be typed. Another page is another field.
  late final TextEditingController _search = TextEditingController(text: text(widget.page.state['searchText']));

  @override
  void dispose() {
    _search.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final state = widget.page.state;
    final kind = _kind(widget.page);
    final animals = objects(state['filteredAnimals']);
    final hostsDetail = state['hostsDetail'] == true;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Text(kind.isEmpty ? '' : kind[0].toUpperCase() + kind.substring(1), style: Theme.of(context).textTheme.headlineMedium),
        const SizedBox(height: 12),
        if (hostsDetail)
          TextField(
            controller: _search,
            decoration: const InputDecoration(prefixIcon: Icon(Icons.search), hintText: 'Enter search term', border: OutlineInputBorder()),
            onChanged: (value) => app.set(widget.target, 'searchText', value),
          ),
        if (animals.isEmpty) Padding(padding: const EdgeInsets.all(12), child: Text('No $kind found.')),
        for (final animal in animals)
          ListTile(
            leading: photo(animal['thumbnailUrl'], width: 46, height: 56, radius: BorderRadius.circular(8)),
            title: Text(text(animal['name'])),
            subtitle: Text(text(animal['location'])),
            // A tap is a route: the server navigates and the page that arrives replaces this one.
            onTap: hostsDetail ? () => app.goTo('/$kind/${Uri.encodeComponent(text(animal['name']))}') : null,
          ),
      ],
    );
  }
}

class AnimalDetailPage extends StatelessWidget {
  const AnimalDetailPage({super.key, required this.page});

  final rt.Page page;

  @override
  Widget build(BuildContext context) {
    final animal = page.state['animal'] == null ? null : rt.asMap(page.state['animal']);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        if (animal != null) ...[
          Text(text(animal['name']), style: Theme.of(context).textTheme.headlineMedium),
          Text(text(animal['location']), style: Theme.of(context).textTheme.titleSmall),
          const SizedBox(height: 12),
          photo(animal['photoUrl'], width: 260, height: 330, radius: BorderRadius.circular(12)),
          const SizedBox(height: 12),
          Text(text(animal['details'])),
        ] else ...[
          Text('Animal not found', style: Theme.of(context).textTheme.headlineMedium),
          const Text('This animal was not found.'),
        ],
        const SizedBox(height: 16),
        OutlinedButton.icon(
          icon: const Icon(Icons.arrow_back),
          label: const Text('Back to list'),
          onPressed: () => app.goTo('/${_kind(page)}'),
        ),
      ],
    );
  }
}
