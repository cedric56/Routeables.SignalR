# Routeables with Flutter

The same view models and the same server as the [Angular sample](../Angular), drawn by a Flutter app: on the web, on Windows, macOS or Linux, on Android or iOS. The server runs the view models; the app receives their state and sends what the user does, through [routeables_client](../../clients/dart), the Dart client of Routeables.SignalR. Every page of the menu is there: the animal lists and details, History, Regions, Adopt a pet, Dialogs (messages, content and task dialogs, notifications, the note modal), Features (guard, lock, measures), Members, Tabs and About, with the side menu and the theme button.

```
Flutter app (xanimals_flutter)  ──SignalR──▶  XAnimals.AngularServer
  draws page.state of each target              Routeables.SignalR: one router, one set of regions and one set of
  app.goTo('/cats/Abyssinian')                 view models per connection
  app.run('', state['pushCommand'])            the view models of XAnimals.ViewModels
  app.answer(dialog, 0)
```

## Run it

You need the .NET 10 SDK and Flutter 3.24 or later.

```bash
# 1. the server, on http://localhost:5090
dotnet run --project samples/Angular/XAnimals.AngularServer

# 2. once: the platform folders (flutter create), and what lets them reach a server in plain HTTP
cd samples/Flutter/xanimals_flutter
flutter pub get
dart run tool/setup.dart

# 3. the app
flutter run -d chrome --web-port 5179      # the web
flutter run -d windows                     # or macos, linux
flutter run                                # an Android emulator or an iOS simulator that is running
```

The app reaches the hub at `http://localhost:5090/routeables`, or `http://10.0.2.2:5090/routeables` from the Android emulator (its name for the machine that runs it). Another address is given at build time:

```bash
flutter run --dart-define=ROUTEABLES_URL=http://192.168.1.10:5090/routeables
```

A phone needs the address of the machine on the network, and a server that listens on it: `dotnet run --project samples/Angular/XAnimals.AngularServer --urls http://0.0.0.0:5090`.

`tool/setup.dart` runs `flutter create` (which keeps `lib`, `test` and `pubspec.yaml`), then allows plain HTTP: `usesCleartextTraffic` on Android, the network client entitlement on macOS, local networking on iOS. These are for a sample that talks to a server on this machine; an application in production uses HTTPS.

`flutter test` draws every page with a state as the server sends it.

## What is worth reading

- `lib/app_state.dart`: the connection as a `ChangeNotifier`: the page, the modal, the menu (`$shell`), the dialogs, the notifications. It is the Flutter counterpart of `routeables.ts` in the web samples.
- `lib/page_view.dart`: the view model decides which page it is; this chooses the widget. The key is the number of the page: another page is another widget, and its search field starts again.
- `lib/region.dart`: a region zone, the way a `RouteableRegion` is on the other platforms; `FollowsTarget` follows a target while the widget is there (the Adopt page follows `$pets` with it).
- `lib/interactions.dart`: the dialogs, the notifications and the modal of the server, drawn above the page.
- `lib/pages/`: one widget per view model. A button runs the `...Command` of the state it was sent; it is disabled when the command cannot run.

## Limits

- The photos come from the same web addresses as in the other samples; Flutter web loads them only when the server of the photos allows it (CORS).
- The server of the sample allows the pages of this machine (CORS) for Flutter web: a sample, not a policy for production.
- There is no address bar on a phone: the menu and the buttons of the pages navigate; the back button of the system leaves the app.
- A connection is a session: when it drops, the server keeps the view models for 30 seconds and the client resumes them (the page, its state, the dialog that waits). After that, the client opens the last page again on a new session.
- The logo of the About page and of the Regions page is drawn as text: Flutter does not draw SVG without a package.
