# Routeables.SignalR

Serves the view models of a [Routeables](https://github.com/cedric56/Routeables) application to a browser front end (Angular, Vue or any JavaScript client) over SignalR. The view models stay on the server; the browser shows their state and calls their commands.

| Folder | What it holds |
| --- | --- |
| [src/Routeables.SignalR](src/Routeables.SignalR) | The server library (`AddRouteablesSignalR`, `MapRouteables`). |
| [clients/typescript](clients/typescript) | `@routeables/client`, the client that does not depend on Angular or Vue. |
| [samples/Angular](samples/Angular) | The Angular application and its server. |
| [samples/Vue](samples/Vue) | The Vue application, which talks to the Angular sample server. |
| [samples/SignalR](samples/SignalR) | CounterServer, the smallest server. |
| [tests](tests) | The tests of the sessions, the hub and the Angular sample server. |

## Build

The projects reference the sources of Routeables, so check both repositories out side by side:

```
git clone https://github.com/cedric56/Routeables
git clone https://github.com/cedric56/Routeables.SignalR
cd Routeables.SignalR
dotnet build Routeables.SignalR.slnx
```

Routeables is expected in `../Routeables`; set `-p:RouteablesRoot=<path>\` (with the trailing separator) to use another place. The Angular sample server also uses the view models of the XAnimals samples of Routeables (`samples/Bases`).

Start with [src/Routeables.SignalR/README.md](src/Routeables.SignalR/README.md).
