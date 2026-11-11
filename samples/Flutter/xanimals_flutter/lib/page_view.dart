import 'package:flutter/material.dart';
import 'package:routeables_client/routeables_client.dart' as rt;

import 'pages/about.dart';
import 'pages/adopt.dart';
import 'pages/animals.dart';
import 'pages/dialogs.dart';
import 'pages/essentials.dart';
import 'pages/features.dart';
import 'pages/history.dart';
import 'pages/layout.dart';
import 'pages/members.dart';
import 'pages/regions.dart';
import 'pages/tabs.dart';

/// How a page is drawn: its page and the target that shows it give a widget.
typedef RouteablePageBuilder = Widget Function(rt.Page page, String target);

const _lists = ['CatsViewModel', 'DogsViewModel', 'MonkeysViewModel', 'ElephantsViewModel', 'BearsViewModel', 'PandasViewModel'];
const _details = ['CatDetailViewModel', 'DogDetailViewModel', 'MonkeyDetailViewModel', 'ElephantDetailViewModel', 'BearDetailViewModel', 'PandaDetail'];

final Map<String, RouteablePageBuilder> _pages = {
  'AboutViewModel': (page, target) => AboutPage(page: page, target: target),
  'AdoptViewModel': (page, target) => AdoptPage(page: page, target: target),
  'DialogsViewModel': (page, target) => DialogsPage(page: page, target: target),
  'EssentialsViewModel': (page, target) => EssentialsPage(page: page, target: target),
  'FeaturesViewModel': (page, target) => FeaturesPage(page: page, target: target),
  'HistoryViewModel': (page, target) => HistoryPage(page: page, target: target),
  'MembersViewModel': (page, target) => MembersPage(page: page, target: target),
  'RegionsViewModel': (page, target) => RegionsPage(page: page, target: target),
  'LayoutBarViewModel': (page, target) => LayoutBar(page: page),
  'LayoutMenuViewModel': (page, target) => LayoutMenu(page: page, target: target),
  'LogoViewModel': (page, target) => LayoutLogo(page: page),
  'TabsViewModel': (page, target) => const TabsPage(),
};

/// Chooses how to draw a page: the view model decides which page it is, this picks the widget for it. The same widget draws the page of
/// the main router and the one a region shows, because a page does not know where it is shown. Another page is another widget, even of
/// the same type (the key is the number of the page): what it held (a search field) starts again.
class RouteablePageView extends StatelessWidget {
  const RouteablePageView({super.key, required this.page, required this.target});

  final rt.Page page;
  final String target;

  @override
  Widget build(BuildContext context) {
    final viewModel = page.viewModel;
    final RouteablePageBuilder? builder = _lists.contains(viewModel)
        ? (rt.Page page, String target) => AnimalListPage(page: page, target: target)
        : _details.contains(viewModel)
            ? (rt.Page page, String target) => AnimalDetailPage(page: page)
            : _pages[viewModel];
    if (builder == null) return Text('No view for $viewModel.');
    return KeyedSubtree(key: ValueKey(page.sequence), child: builder(page, target));
  }
}
