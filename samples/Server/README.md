# XAnimals.Server

The server of every sample: Angular, Vue, React and Flutter draw the same view models, and this server runs them. It is an ASP.NET Core application with Routeables.SignalR: one router, one set of regions and one set of view models per connection, on http://localhost:5090.

```
Angular · Vue · React · Flutter  ──SignalR (/routeables)──▶  XAnimals.Server
                                                             the view models of XAnimals.ViewModels (Routeables, samples/Bases)
```

## Run it

```bash
dotnet run --project samples/Server          # http://localhost:5090
```

Then start one of the apps ([Angular](../Angular), [Vue](../Vue), [React](../React), [Flutter](../Flutter)), or several: each one has its own port and its own sessions. The address comes from `appsettings.json` (`Urls`), wherever the server is started from (Visual Studio, `dotnet run`, its exe); `--urls` or `ASPNETCORE_URLS` choose another one. A phone on the network needs `--urls http://0.0.0.0:5090`.

In Visual Studio, the launch profiles of `Routeables.SignalR.slnLaunch` start it with the apps (**Angular + server**, **Vue + server**, **React + server**, **All web apps + server**).

## What is worth reading

- `Extensions.cs`: the whole server. A route is registered with its view model and no view; `$shell` (the menu and the theme) and `$pets` (the cards of Adopt) are view models offered to the apps; `NoteModal` is the modal.
- `Program.cs`: the hub (`MapRouteables`), and the CORS policy that lets the apps of the machine reach it.
- `ShellViewModel.cs`, `PetsViewModel.cs`: view models written for this server, marked `[RouteableState]`.

What crosses the network: the view models of the XAnimals libraries cannot carry `[RouteableState]` (they are shared with the desktop samples of Routeables), so the server exposes their types by namespace (`AddAnimalsExposure` in `Extensions.cs`, settable: the apps have text boxes). An application of your own annotates its view models instead.

The tests in [tests/Routeables.SignalR.Test.Angular](../../tests/Routeables.SignalR.Test.Angular) drive this server as an app does, without a browser; [tests/samples](../../tests/samples) drives each web app in Chrome against it.

> **A sample, not a template for production.** This server accepts any connection, without authentication, and allows the pages of the machine (any `localhost` origin, with credentials) so that every web sample and Flutter web reach it. Do not copy its `Program.cs` as it is: protect the hub (`MapRouteables().RequireAuthorization()`), allow only your own origins, and go through the checklist of [SECURITY.md](../../SECURITY.md).
