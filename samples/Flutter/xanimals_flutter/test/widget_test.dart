import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:routeables_client/routeables_client.dart' as rt;
import 'package:xanimals_flutter/page_view.dart';

/// A page as the server would send it.
rt.Page page(String viewModel, Map<String, Object?> state, {String route = '/cats'}) => rt.Page(
      sequence: 1,
      target: rt.pageTarget,
      route: route,
      viewModel: viewModel,
      state: state,
      commands: const [],
      generation: 1,
      version: 0,
    );

Map<String, Object?> command(String path, {bool canExecute = true}) => {r'$command': path, 'canExecute': canExecute};

/// Draws a page the way the app does, without a server: the widgets of the pages must lay out with what the server sends.
Future<void> draw(WidgetTester tester, rt.Page page) => tester.pumpWidget(MaterialApp(
      home: Scaffold(body: SingleChildScrollView(child: RouteablePageView(page: page, target: rt.pageTarget))),
    ));

void main() {
  testWidgets('a list of animals, with its search and its items', (tester) async {
    await draw(tester, page('CatsViewModel', {
      'hostsDetail': true,
      'searchText': 'aby',
      'filteredAnimals': [
        {'name': 'Abyssinian', 'location': 'Ethiopia', 'thumbnailUrl': '', 'photoUrl': ''},
      ],
    }));

    expect(find.text('Cats'), findsOneWidget);
    expect(find.text('Abyssinian'), findsOneWidget);
    expect(find.widgetWithText(TextField, 'aby'), findsOneWidget);
  });

  testWidgets('a detail, and an animal that is not found', (tester) async {
    await draw(tester, page('CatDetailViewModel', {
      'animal': {'name': 'Abyssinian', 'location': 'Ethiopia', 'details': 'A cat.', 'photoUrl': ''},
    }, route: '/cats/Abyssinian'));
    expect(find.text('Abyssinian'), findsOneWidget);
    expect(find.text('Back to list'), findsOneWidget);

    await draw(tester, page('PandaDetail', {'animal': null}, route: '/pandas/Nobody'));
    expect(find.text('Animal not found'), findsOneWidget);
  });

  testWidgets('the dialogs page: a disabled command is a disabled button', (tester) async {
    await draw(tester, page('DialogsViewModel', {
      'okCommand': command('okCommand'),
      'modalCommand': command('modalCommand', canExecute: false),
      'hasResult': true,
      'result': 'You chose Yes',
    }, route: '/dialogs'));

    expect(find.text('Ok'), findsOneWidget);
    expect(find.text('You chose Yes'), findsOneWidget);
    final modal = tester.widget<FilledButton>(find.ancestor(of: find.text('Show modal'), matching: find.byType(FilledButton)));
    expect(modal.onPressed, isNull);
  });

  testWidgets('the pages with regions lay out before their regions arrive', (tester) async {
    await draw(tester, page('HistoryViewModel', {
      'entries': [
        {'number': 1, 'name': 'Cats', 'glyph': '🐱', 'caption': '/cats', 'badge': 'Push', 'isCurrent': true, 'isAhead': false},
      ],
      'transitions': ['None', 'Fade'],
      'transition': 'Fade',
      'canGoBack': false,
      'canGoForward': false,
      'guardGlyph': '☐',
      'guardText': 'Block leaving',
      'status': 'Ready',
    }, route: '/history'));
    expect(find.text('☐ Block leaving'), findsOneWidget);

    await draw(tester, page('RegionsViewModel', {'isLeftCollapsed': false, 'creditsText': '© XAnimals'}, route: '/regions'));
    expect(find.text('© XAnimals'), findsOneWidget);

    await draw(tester, page('TabsViewModel', {}, route: '/tabs'));
    expect(find.text('Dogs'), findsOneWidget);
  });

  testWidgets('the other pages', (tester) async {
    await draw(tester, page('FeaturesViewModel', {'isSignedOut': true, 'canLock': true, 'link': '/cats', 'measures': '', 'status': ''}, route: '/features'));
    expect(find.text('Lock for 5 seconds'), findsOneWidget);

    await draw(tester, page('AdoptViewModel', {
      'choices': [
        {'name': 'Tom', 'title': 'Adopt Tom', 'adoptCommand': command('choices.0.adoptCommand')},
      ],
      'status': 'Nobody yet',
    }, route: '/adopt'));
    expect(find.text('Adopt Tom'), findsOneWidget);

    await draw(tester, page('MembersViewModel', {'welcome': 'Welcome, member'}, route: '/members'));
    expect(find.text('Welcome, member'), findsOneWidget);

    await draw(tester, page('AboutViewModel', {'polarBearsCommand': command('polarBearsCommand')}, route: '/about'));
    expect(find.text('Polar bears'), findsOneWidget);

    await draw(tester, page('EssentialsViewModel', {
      'summary': 'This client announced 2 services: 2 of the 3 tests can run.',
      'capabilities': ['battery', 'clipboard'],
      'checks': [
        {'group': 'Device', 'name': 'Battery', 'description': 'IBattery', 'label': 'Read', 'supported': true, 'status': 'ok', 'result': '80 %', 'runCommand': command('checks.0.runCommand')},
        {'group': 'Communication', 'name': 'Clipboard', 'description': 'IClipboard', 'label': 'Copy and paste', 'supported': true, 'status': 'ready', 'result': '', 'runCommand': command('checks.1.runCommand')},
        {'group': 'Media', 'name': 'Text to speech', 'description': 'ITextToSpeech', 'label': 'Speak', 'supported': false, 'status': 'unsupported', 'result': '', 'runCommand': command('checks.2.runCommand')},
      ],
    }, route: '/essentials'));
    expect(find.text('Communication'), findsOneWidget);
    expect(find.text('80 %'), findsOneWidget);
    final speak = tester.widget<FilledButton>(find.ancestor(of: find.text('Speak'), matching: find.byType(FilledButton)));
    expect(speak.onPressed, isNull);

    await draw(tester, page('UnknownViewModel', {}));
    expect(find.text('No view for UnknownViewModel.'), findsOneWidget);
  });
}
