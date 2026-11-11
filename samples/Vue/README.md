# Routeables with Vue

The same view models and the same server as the [Angular sample](../Angular), drawn by a Vue 3 app. The server runs the view models; the browser receives their state and sends what the user does. Every page of the menu is there: the animal lists and details, History, Regions, Adopt a pet, Dialogs (messages, content and task dialogs, notifications, the note modal), Features (guard, lock, measures), Members, Tabs, Essentials (the device APIs of the browser, tested from the server) and About, with the same side panel and theme button as the other web samples.

```
Vue app (xanimals-vue)  ──SignalR──▶  XAnimals.Server (samples/Server)
  shows page.state of each target       Routeables.SignalR: one router, one set of regions and one set of
  goTo('/cats/Abyssinian')              view models per connection
  run('', state.pushCommand)            the view models of XAnimals.ViewModels
  answer(dialog, 0)
```

There is no second server: a front end only needs the hub `/routeables`, so the Vue app uses the one of the Angular sample.

## Run it

Two terminals. You need the .NET 10 SDK and Node 20.19, 22.12 or later.

```bash
# 1. the server, on http://localhost:5090
dotnet run --project samples/Server

# 2. the app, on http://localhost:5173 (it proxies /routeables to the server)
cd samples/Vue/XAnimals.Vue
npm install
npm start
```

Open http://localhost:5173. Use the side panel; reload on `/dogs/Afghan%20Hound` or `/tabs`: the address is a route. Open Features and press "Lock for 5 seconds": the menu locks, and unlocks by itself when the server says so.

`npm run build` checks the types (`vue-tsc`) and builds the app into `dist`.

## In Visual Studio

Open `Routeables.SignalR.slnx`: the app is the project `XAnimals.Vue` (`XAnimals.Vue.esproj`, the JavaScript project system of Visual Studio). Choose the launch profile **Vue + server** in the toolbar (it comes from `Routeables.SignalR.slnLaunch`) and press F5: Visual Studio starts XAnimals.Server on http://localhost:5090, runs `npm start` and opens http://localhost:5173 in Edge or Chrome with the debugger attached (`.vscode/launch.json`). It runs `npm install` when `node_modules` is missing. **All web apps + server** starts every web sample (Angular, Vue, React) against the same server.

Visual Studio 2022 17.14 or later (or Visual Studio 2026) with the "Node.js development" or "ASP.NET and web development" workload, and Node.js 22.

## What is worth reading

- `src/routeables.ts`: the connection as refs, the address bar that follows the page, the dialogs and the notifications. It is the file to compare with `routeables.ts` of the Angular sample: a `signal` is a `shallowRef`, the rest is the same.
- `src/PageView.vue`: the view model decides which page it is; this chooses the component. The same component draws the page of the main router and the page a region shows.
- `src/Region.vue`: a region zone, the way a `RouteableRegion` is on the other platforms. The `key` is the number of the page, so a page that arrives plays the transition again.
- `src/pages/`: one component per view model. A button runs the `...Command` of the state it was sent.

The app imports `@routeables/client` from `clients/typescript/src` (see `vite.config.ts` and `tsconfig.json`), so there is nothing to build or publish first. The look is the one of the Angular sample (`public/css`).

## Limits

- The photos come from the same web addresses as in the other samples.
- A connection is a session: when it drops, the server keeps the view models for 30 seconds and the client resumes them (the page, its state, the dialog that waits). After that, the client opens the last page again on a new session.
- With several servers, a client must always reach the same one (sticky sessions); a backplane does not help, a session lives on one server.
- The Birds module has no page here, and the notification of the operating system ("System") is a notification of the page.
