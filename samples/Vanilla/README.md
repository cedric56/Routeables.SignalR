# Routeables with vanilla TypeScript

The same view models and the same server as the [Angular sample](../Angular), drawn by a TypeScript and the DOM, without a framework app. The server runs the view models; the browser receives their state and sends what the user does. Every page of the menu is there: the animal lists and details, History, Regions, Adopt a pet, Dialogs (messages, content and task dialogs, notifications, the note modal), Features (guard, lock, measures), Members, Tabs and About, with the same side panel and theme button as the other web samples.

```
vanilla TypeScript app (xanimals-vanilla)  ──SignalR──▶  XAnimals.AngularServer
  shows page.state of each target       Routeables.SignalR: one router, one set of regions and one set of
  goTo('/cats/Abyssinian')              view models per connection
  run('', state.pushCommand)            the view models of XAnimals.ViewModels
  answer(dialog, 0)
```

There is no second server: a front end only needs the hub `/routeables`, so the app uses the one of the Angular sample.

## Run it

Two terminals. You need the .NET 10 SDK and Node 22.12 or later.

```bash
# 1. the server, on http://localhost:5090
dotnet run --project samples/Angular/XAnimals.AngularServer

# 2. the app, on http://localhost:5178 (it proxies /routeables to the server)
cd samples/Vanilla/XAnimals.Vanilla
npm install
npm start
```

Open http://localhost:5178. Use the side panel; reload on `/dogs/Afghan%20Hound` or `/tabs`: the address is a route. Open Features and press "Lock for 5 seconds": the menu locks, and unlocks by itself when the server says so.

`npm run build` checks the types (`tsc`) and builds the app into `dist`.

## In Visual Studio

Open `Routeables.SignalR.slnx`: the app is the project `XAnimals.Vanilla` (`XAnimals.Vanilla.esproj`). Choose the launch profile **Vanilla + server** in the toolbar (`Routeables.SignalR.slnLaunch`) and press F5: Visual Studio starts XAnimals.AngularServer on http://localhost:5090, runs `npm start` and opens http://localhost:5178 in Edge or Chrome with the debugger attached (`.vscode/launch.json`). It runs `npm install` when `node_modules` is missing. **All web apps + server** starts every web sample against the same server.

## What is worth reading

- `src/routeables.ts`: the connection, the address bar that follows the page, the dialogs and the notifications, as one snapshot. The same file is in every web sample.
- `src/dom.ts`: `h()` creates an element, `View` is what is drawn for a page (`update` when its state changes, `destroy` when it goes away).
- `src/page-view.ts`: the view model decides which page it is; `PageSlot` updates the view of the same page and replaces it when another page arrives.
- `src/region.ts`: a region zone, the way a `RouteableRegion` is on the other platforms.
- `src/pages/`: one view per view model. A page with a text box or a region creates them once and draws the rest again.

The app imports `@routeables/client` from `clients/typescript/src` (see `vite.config.ts` and `tsconfig.json`), so there is nothing to build or publish first. The look is the one of the Angular sample (`public/css`). The tab icon is the one of the template of the framework (create-vite).

## Limits

- The photos come from the same web addresses as in the other samples.
- A connection is a session: when it drops, the server keeps the view models for 30 seconds and the client resumes them (the page, its state, the dialog that waits). After that, the client opens the last page again on a new session.
- With several servers, a client must always reach the same one (sticky sessions); a backplane does not help, a session lives on one server.
- The Birds module has no page here, and the notification of the operating system ("System") is a notification of the page.
