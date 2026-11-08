# Routeables.SignalR

Serves the view models of a [Routeables](https://github.com/cedric56/Routeables) application to a browser front end (Angular, Vue or any JavaScript client) over SignalR. The view models stay on the server, as with Blazor Server; the browser shows their state and calls their commands. The front end keeps its own components: only the application logic is shared.

```csharp
// Server
builder.Services.AddRouteablesSignalR(services =>
    services.RegisterRoute<OrdersView, OrdersViewModel>(route: "/orders"));

app.UseAuthentication();
app.UseAuthorization();
app.MapRouteables().RequireAuthorization();   // the hub, at /routeables

[RouteableState]                                         // what the browser may see
public sealed class OrdersViewModel : INotifyPropertyChanged
{
    public IReadOnlyList<Order> Orders { get; private set; }

    [RouteableState(Settable = true)]                    // what the browser may set
    public string Search { get; set; } = "";

    [RouteableCommand, Authorize(Roles = "manager")]     // what the browser may call, and who
    public Task ApproveAsync(int id) => ...;
}
```

```ts
// Browser
import { RouteablesClient, signalRTransport } from '@routeables/client';

const client = new RouteablesClient(signalRTransport('/routeables'));
client.subscribe(page => render(page?.state));
await client.connect();
await client.goTo('/orders');
await client.invoke('approveAsync', 42);   // resolves when the command has ended, its changes already applied
```

## What it gives

- **Nothing leaves the server unless it says so.** Properties are sent when marked `[RouteableState]`, set when marked `Settable`, called when marked `[RouteableCommand]`.
- **Authorization at every call**, with ASP.NET Core's policies: `[Authorize]` on view models and commands, `[RouteableAuthorize]` on properties, a route guard for pages.
- **An ordered, consistent state.** One dispatcher per session (a view model is never used by two threads at once), one stream per target (generations and versions), patches instead of whole states, changes merged for slow clients.
- **Dialogs, notifications and modals** that a view model awaits as on a desktop, without blocking the connection.
- **Reconnection.** A session survives a dropped connection for 30 seconds and is resumed by its user: pages, state and the dialogs that wait.
- **Observability.** Logs, an activity per call, and metrics (`Routeables.SignalR`).

## What is in the repository

| Folder | What it holds |
| --- | --- |
| [src/Routeables.SignalR](src/Routeables.SignalR) | The server library (`AddRouteablesSignalR`, `MapRouteables`). |
| [clients/typescript](clients/typescript) | `@routeables/client`, the client; it does not depend on Angular or Vue. |
| [clients/dart](clients/dart) | `routeables_client`, the client for Dart and Flutter, with the same protocol and the same guarantees. |
| [samples/Angular](samples/Angular) | The Angular application and its server, XAnimals.AngularServer. |
| [samples/Vue](samples/Vue), [samples/React](samples/React) | The same application in Vue and React, against the same server. |
| [samples/Flutter](samples/Flutter) | The same application in Flutter: web, desktop and mobile. |
| [tests](tests) | The tests of the sessions, the hub, the security, the protocol, the reconnection and the Angular sample server; [tests/e2e](tests/e2e) drives a real Kestrel and SignalR server from Node; [tests/samples](tests/samples) drives every web sample in Chrome. |

## Build

The projects reference the sources of Routeables, so check both repositories out side by side:

```
git clone https://github.com/cedric56/Routeables
git clone https://github.com/cedric56/Routeables.SignalR
cd Routeables.SignalR
dotnet build Routeables.SignalR.slnx
```

Needs the .NET 10 SDK and Node.js 22. Routeables is expected in `../Routeables`; set `-p:RouteablesRoot=<path>\` (with the trailing separator) to use another place. The Angular sample server also uses the view models of the XAnimals samples of Routeables (`samples/Bases`).

The CI builds against one revision of Routeables (`ROUTEABLES_REF` in [.github/workflows/ci.yml](.github/workflows/ci.yml)), so that a change of Routeables never breaks this build by surprise: move it on purpose. To build against the published packages of Routeables instead of its sources, give their version: `dotnet build -p:RouteablesVersion=2.0.0` (the release does so: the NuGet package depends on packages only).

## Run the samples

Every web sample is the same application (the XAnimals pages, the menu, the regions, the dialogs, the notifications, the modal) drawn by another front end, against one server:

| Sample | Front end | Address |
| --- | --- | --- |
| [Angular](samples/Angular) | Angular 21 (signals) | http://localhost:4200 |
| [Vue](samples/Vue) | Vue 3 | http://localhost:5173 |
| [React](samples/React) | React 19 | http://localhost:5174 |
| [Flutter](samples/Flutter) | Flutter (web, Windows, macOS, Linux, Android, iOS) | http://localhost:5179 on the web (`flutter run -d chrome --web-port 5179`) |

```
dotnet run --project samples/Angular/XAnimals.AngularServer          # the server, on http://localhost:5090
cd samples/React/XAnimals.React && npm install && npm start          # or any other sample folder
```

They can run at the same time: each one has its own port and its own sessions on the server.

## Visual Studio

Open `Routeables.SignalR.slnx` (Visual Studio 2022 17.14 or later, with the "ASP.NET and web development" or "Node.js development" workload). The web samples and the TypeScript client are JavaScript projects (`.esproj`) of the solution. The launch profiles of `Routeables.SignalR.slnLaunch` start the server and the apps together: choose **Angular + server**, **Vue + server**, **React + server** or **All web apps + server** next to the Start button and press F5. Visual Studio runs `npm install` when `node_modules` is missing; outside Visual Studio the JavaScript projects run neither `npm install` nor their build (the CI uses npm).

## Test

```
dotnet test --project tests/Routeables.SignalR.Test/Routeables.SignalR.Test.csproj
dotnet test --project tests/Routeables.SignalR.Test.Angular/Routeables.SignalR.Test.Angular.csproj
npm --prefix clients/typescript ci && npm --prefix clients/typescript test

# End to end: a real Kestrel and SignalR server, driven by the client from Node
dotnet build tests/Routeables.SignalR.E2E.Server -c Release
npm --prefix clients/typescript run build
node --test tests/e2e/e2e.test.mjs

# A web sample in Chrome, against the server (started as above, and the app built and served by `npm run preview`)
npm --prefix tests/samples ci && node tests/samples/smoke.mjs http://localhost:5174

# The Dart client and the Flutter sample
(cd clients/dart && flutter test)
(cd samples/Flutter/xanimals_flutter && dart run tool/setup.dart && flutter test)
```

## Documentation

- [src/Routeables.SignalR/README.md](src/Routeables.SignalR/README.md): the server — what crosses the network, authorization, the session, the messages, the limits, the options, deployment.
- [clients/typescript/README.md](clients/typescript/README.md) and [clients/dart/README.md](clients/dart/README.md): the clients — order, answers, reconnection.
- The README of each sample ([Angular](samples/Angular/README.md), [Vue](samples/Vue/README.md), [React](samples/React/README.md), [Flutter](samples/Flutter/README.md)).
- [SECURITY.md](SECURITY.md): the security model and the checklist before production.
- [VERSIONING.md](VERSIONING.md) and [CHANGELOG.md](CHANGELOG.md): versions, the protocol, releases.

## License

MIT, see [LICENSE.txt](LICENSE.txt).
