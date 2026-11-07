# Routeables.SignalR

Serves the view models of a [Routeables](https://github.com/cedric56/Routeables) application to a browser front end (Angular, Vue or any JavaScript client) over SignalR. The view models stay on the server; the browser shows their state and calls their commands.

| Folder | What it holds |
| --- | --- |
| [src/Routeables.SignalR](src/Routeables.SignalR) | The server library (`AddRouteablesSignalR`, `MapRouteables`). |
| [clients/typescript](clients/typescript) | `@routeables/client`, the client that does not depend on Angular or Vue. |
| [samples/Angular](samples/Angular) | The Angular application and its server. |
| [samples/Vue](samples/Vue) | The Vue application, which talks to the Angular sample server. |
| [samples/SignalR](samples/SignalR) | CounterServer, the smallest server. |
| [tests](tests) | The tests of the sessions, the hub, the security, the protocol, the reconnection and the Angular sample server; [tests/e2e](tests/e2e) drives a real SignalR server from Node. |

## Build

The projects reference the sources of Routeables, so check both repositories out side by side:

```
git clone https://github.com/cedric56/Routeables
git clone https://github.com/cedric56/Routeables.SignalR
cd Routeables.SignalR
dotnet build Routeables.SignalR.slnx
```

Routeables is expected in `../Routeables`; set `-p:RouteablesRoot=<path>\` (with the trailing separator) to use another place. The Angular sample server also uses the view models of the XAnimals samples of Routeables (`samples/Bases`).

The CI builds against one revision of Routeables (`ROUTEABLES_REF` in [.github/workflows/ci.yml](.github/workflows/ci.yml)), so that a change of Routeables never breaks this build by surprise; move it on purpose. To build against the published packages of Routeables instead of its sources, give their version: `dotnet build -p:RouteablesVersion=2.0.0` (the release does so: the NuGet package depends on packages only).

## Test

```
dotnet test --project tests/Routeables.SignalR.Test/Routeables.SignalR.Test.csproj
dotnet test --project tests/Routeables.SignalR.Test.Angular/Routeables.SignalR.Test.Angular.csproj
npm --prefix clients/typescript ci && npm --prefix clients/typescript test

# End to end: a real Kestrel and SignalR server, driven by the client from Node
dotnet build tests/Routeables.SignalR.E2E.Server -c Release
npm --prefix clients/typescript run build
node --test tests/e2e/e2e.test.mjs
```

## Documentation

- [src/Routeables.SignalR/README.md](src/Routeables.SignalR/README.md): the server — what crosses the network, authorization, the session, the messages, the limits, deployment.
- [clients/typescript/README.md](clients/typescript/README.md): the client — order, answers, reconnection.
- [SECURITY.md](SECURITY.md): the security model and the checklist before production.
- [VERSIONING.md](VERSIONING.md) and [CHANGELOG.md](CHANGELOG.md): versions, the protocol, releases.
