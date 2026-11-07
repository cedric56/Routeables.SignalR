import 'package:flutter/material.dart';
import 'package:routeables_client/routeables_client.dart' as rt;

import 'app_state.dart';
import 'interactions.dart';
import 'page_view.dart';

/// The application: the bar, the menu, the page of the main router, and what the services of the server show (above everything).
class XAnimalsApp extends StatefulWidget {
  const XAnimalsApp({super.key});

  @override
  State<XAnimalsApp> createState() => _XAnimalsAppState();
}

class _XAnimalsAppState extends State<XAnimalsApp> {
  @override
  void initState() {
    super.initState();
    app.start();
  }

  // The theme the view models asked for (IRouteableRequestedThemeService). Default follows the system.
  ThemeMode _mode(String theme) => switch (theme.toLowerCase()) {
        'light' => ThemeMode.light,
        'dark' => ThemeMode.dark,
        _ => ThemeMode.system,
      };

  @override
  Widget build(BuildContext context) => ListenableBuilder(
        listenable: app,
        builder: (context, _) {
          const seed = Color(0xFF1B6B57); // the canopy green of the XAnimals samples
          return MaterialApp(
            title: 'Routeables Animals',
            debugShowCheckedModeBanner: false,
            themeMode: _mode(app.theme),
            theme: ThemeData(colorSchemeSeed: seed, brightness: Brightness.light),
            darkTheme: ThemeData(colorSchemeSeed: seed, brightness: Brightness.dark),
            home: const Shell(),
          );
        },
      );
}

/// The bar, the menu (one entry per entry of the main view model) and the page; above them, what the services of the server show.
class Shell extends StatelessWidget {
  const Shell({super.key});

  @override
  Widget build(BuildContext context) => ListenableBuilder(
        listenable: app,
        builder: (context, _) {
          final page = app.page;
          final wide = MediaQuery.sizeOf(context).width >= 900;
          final first = page?.route.split('/').where((segment) => segment.isNotEmpty).firstOrNull ?? '';
          final menu = SideMenu(current: '/$first', closeDrawer: !wide);

          final scaffold = Scaffold(
            appBar: AppBar(
              title: const Text('Routeables Animals'),
              actions: [
                // The theme is asked of the view model like on the other platforms; Default follows the system.
                IconButton(
                  tooltip: 'Theme: ${app.theme} (tap for the next one)',
                  icon: const Icon(Icons.contrast),
                  onPressed: () => app.run(r'$shell', app.shell?.state['themeCommand']),
                ),
              ],
            ),
            drawer: wide ? null : Drawer(child: menu),
            body: Row(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                if (wide) SizedBox(width: 280, child: Material(elevation: 1, child: menu)),
                Expanded(
                  child: SingleChildScrollView(
                    padding: const EdgeInsets.all(20),
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.stretch,
                      children: [
                        if (app.error != null)
                          Padding(
                            padding: const EdgeInsets.only(bottom: 12),
                            child: Text(app.error!, style: TextStyle(color: Theme.of(context).colorScheme.error)),
                          ),
                        if (page != null)
                          RouteablePageView(page: page, target: rt.pageTarget)
                        else
                          Text(app.connected ? 'Loading…' : 'Offline: the server is not reachable.'),
                      ],
                    ),
                  ),
                ),
              ],
            ),
          );

          // The dialogs, the notifications and the modal are drawn above the page.
          return Stack(children: [scaffold, const Interactions()]);
        },
      );
}

/// The side panel. A page published a lock (Features): its entries cannot be followed until it publishes the unlock.
class SideMenu extends StatelessWidget {
  const SideMenu({super.key, required this.current, required this.closeDrawer});

  /// The first segment of the address of the page: /cats/Tom is still in Cats.
  final String current;
  final bool closeDrawer;

  @override
  Widget build(BuildContext context) => ListView(
        children: [
          for (final entry in app.entries)
            ListTile(
              leading: Text(entry.glyph, style: const TextStyle(fontSize: 22)),
              title: Text(entry.name),
              subtitle: entry.description.isEmpty ? null : Text(entry.description, maxLines: 1, overflow: TextOverflow.ellipsis),
              enabled: !entry.isLocked,
              trailing: entry.isLocked ? const Icon(Icons.lock, size: 18) : null,
              selected: entry.route.toLowerCase() == current.toLowerCase(),
              onTap: () {
                if (closeDrawer) Navigator.of(context).maybePop();
                app.goTo(entry.route);
              },
            ),
        ],
      );
}
