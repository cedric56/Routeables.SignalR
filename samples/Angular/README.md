# Routeables with Angular

The same view models as the other samples, drawn by an Angular app. The server runs them; the browser receives their state and sends what the user does. Every page of the menu is there: the animal lists and details, History, Regions, Adopt a pet, Dialogs (messages, content and task dialogs, notifications, the note modal), Features (guard, lock, measures), Members, Tabs and About, with the same side panel and theme button as the Razor sample.

```
Angular app (xanimals-angular)  ──SignalR──▶  XAnimals.AngularServer
  shows page.state of each target               Routeables.SignalR: one router, one set of regions and one set of
  goTo('/cats/Abyssinian')                      view models per connection
  run('', state.pushCommand)                    the view models of XAnimals.ViewModels
  answer(dialog, 0)                             (one command added: RegionsViewModel.ToggleLeftCommand)
```

## Run it

Two terminals. You need the .NET 10 SDK and Node 20.19, 22.12 or later.

```bash
# 1. the server, on http://localhost:5090
dotnet run --project samples/Angular/XAnimals.AngularServer

# 2. the app, on http://localhost:4200 (it proxies /routeables to the server)
cd samples/Angular/XAnimals.Angular
npm install
npm start
```

Open http://localhost:4200. Use the side panel; reload on `/dogs/Afghan%20Hound` or `/tabs`: the address is a route. Open Features and press "Lock for 5 seconds": the menu locks, and unlocks by itself when the server says so.

## In Visual Studio

Open `Routeables.SignalR.slnx`: the app is the project `XAnimals.Angular` (`XAnimals.Angular.esproj`, the JavaScript project system of Visual Studio). Choose the launch profile **Angular + server** in the toolbar (it comes from `Routeables.SignalR.slnLaunch`) and press F5: Visual Studio starts XAnimals.AngularServer on http://localhost:5090, runs `npm start` and opens http://localhost:4200 in Edge or Chrome with the debugger attached (`.vscode/launch.json`). It runs `npm install` when `node_modules` is missing. **All web apps + server** starts every web sample (Angular, Vue, React, Svelte, Solid, Lit, vanilla) against the same server.

Visual Studio 2022 17.14 or later (or Visual Studio 2026) with the "Node.js development" or "ASP.NET and web development" workload, and Node.js 22.

## What is worth reading

- `XAnimals.AngularServer/Extensions.cs`: the whole server. A route is registered with its view model and no view; `$shell` (the menu and the theme) and `$pets` (the cards of Adopt) are view models offered to the app; `NoteModal` is the modal.
- `XAnimals.Angular/src/app/routeables.ts`: the connection as signals, the address bar that follows the page, the dialogs and the notifications.
- `XAnimals.Angular/src/app/page-view.ts`: the view model decides which page it is; this chooses the component. The same component draws the page of the main router and the page a region shows.
- `XAnimals.Angular/src/app/region.ts`: a region zone, the way a `RouteableRegion` is on the other platforms.
- `XAnimals.Angular/src/app/pages/`: one component per view model. A button runs the `...Command` of the state it was sent.

What crosses the network: the view models of the XAnimals libraries cannot carry `[RouteableState]` (they are shared with the desktop samples), so the server exposes their types by namespace (`AddAnimalsExposure` in `Extensions.cs`, settable: the app has text boxes); the view models written for this server (`ShellViewModel`, `PetsViewModel`) carry `[RouteableState]` themselves. An application of your own annotates its view models instead.

The app imports `@routeables/client` from `clients/typescript/src` (see `paths` in `tsconfig.json`), so there is nothing to build or publish first. The look is the one of the Razor sample (`public/css`).

## Limits

- The photos come from the same web addresses as in the other samples.
- A connection is a session: when it drops, the server keeps the view models for 30 seconds and the client resumes them (the page, its state, the dialog that waits). After that, the client opens the last page again on a new session.
- With several servers, a client must always reach the same one (sticky sessions); a backplane does not help, a session lives on one server.
- The Birds module has no page here (as in the Razor sample), and the notification of the operating system ("System") is a notification of the page.
