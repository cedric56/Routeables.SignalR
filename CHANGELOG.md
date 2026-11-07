# Changelog

## 2.0.0 — unreleased

The first version meant for production. It changes the protocol (2) and some defaults: see "Breaking changes".

### Security

- **Explicit exposure by default.** Only properties marked `[RouteableState]` (or classes marked with it, or types accepted by `RouteablesOptions.ExposeTypes`) are sent; only those marked `[RouteableState(Settable = true)]` can be set by the client. Before, every public property was sent and every public setter could be called. `RouteableExposure.Public` restores that, as a deliberate choice.
- **Authorization of commands, properties and pages**, at every call, with ASP.NET Core's authorization: `[Authorize]` / `[AllowAnonymous]` on a `[RouteableCommand]` method or a view model class, `[RouteableAuthorize]` on a property. A route guard (`RouteableAuthorizationGuard`) cancels a route change to a view model the user may not see, and such a view model is never sent. The commands a user may not call are left out of the page.
- An `ICommand` is reachable only through an exposed property: it was reachable through any public property.
- `RequireAuthenticatedUser` closes anonymous connections; the documentation shows `MapRouteables().RequireAuthorization()`.
- What a client sends is checked before it is read: size, number of values, depth, number of arguments, length of names. No input makes the hub throw.
- The message of an unexpected exception is sent in Development only (`DetailedErrors`); `RouteableException` carries messages meant for the visitor.

### Correctness

- **A dispatcher per session**: calls and changes run one at a time, in order; an `await` comes back to it. Commands of one session never run on two threads at once.
- **One ordered stream per target**: `Navigated` carries a generation, `Changed` a generation and a version. A change of a page that was left is never applied to the next one; a lost change is detected and the state sent again (`Resync`).
- The answer to a call comes after the messages it caused.
- A route change that asks the visitor something (a page that asks before it is left) no longer blocks the connection: it is answered `pending`, like a command that opens a dialog.
- A call answered `pending` ends with `Completed` (success, failure or cancellation), which the client awaits: the answer no longer says "success" for a command that fails later.
- An exception thrown by an `async` command (before or after its first `await`) is a failed answer; it escaped the hub. A cancelled command is reported (`cancelled`). A command returning `ValueTask` is awaited. An `AsyncRelayCommand` is awaited through `ExecuteAsync(object)`.
- The limit of watched targets cannot be passed by concurrent calls, and no longer counts the page and the modal.
- A dialog asked by one call no longer ends the wait of another call.
- A page whose collection changed while it was read is no longer lost: the property is read again.

### Performance and resilience

- Changes are patches: `set` at a path, `splice` of a list (`ObservableCollection` adds, removes, moves, replaces), the item of a list that changed. The changes of one turn are sent once; changes waiting for a slow client are merged; beyond `MaxPendingMessages` the connection is closed and the session resumed.
- Limits of a state: `MaxCollectionItems`, `MaxStateNodes` (the property is sent as `null` and the client told why); a throwing getter or enumerator no longer stops a page.
- The reflection is cached per type; a change resubscribes only the property that changed.
- **Resumable sessions**: a session waits `ReconnectGracePeriod` (30 s) for its client, which resumes it with `Hello` — view models, state, dialogs that wait, and what was told meanwhile. Only by the same user.
- Logs, an activity per call, and metrics (`RouteablesTelemetry`, meter and source `Routeables.SignalR`).

### Client (`@routeables/client`)

- Applies patches by generation and version, asks the state again when a change is missing.
- Resolves a call when it ends (`Completed`), even when the end arrives before the answer.
- Removes a dialog or a notification once the server took the answer; keeps it when the answer did not reach the server.
- `onError` for calls that did not reach the server and watches that were refused.
- Keeps the pages while reconnecting, resumes the session, and recovers a lost one (`recover`, `onSessionLost`).

### Client for Dart and Flutter (`routeables_client`)

- A port of `@routeables/client` to Dart, on the SignalR client for Dart (signalr_netcore): the same protocol, the same guarantees, the same tests.
- A Flutter sample (web, desktop, mobile) against the server of the Angular sample, which allows the pages of the machine (CORS) for Flutter web.

### Build and distribution

- Builds against the current Routeables (`GetEntries` / `IMenuEntry`); the CI pins the revision of Routeables (`ROUTEABLES_REF`).
- The CounterServer sample is removed: the web samples show the library.
- Web samples in React, Svelte, SolidJS, Lit and TypeScript without a framework, beside Angular and Vue: the same application against the same server, each built and driven in Chrome by the CI (`tests/samples/smoke.mjs`).
- `-p:RouteablesVersion=…` builds and packs against the Routeables packages; a release workflow publishes the NuGet and npm packages from a tag, after installing the NuGet package in an empty application.
- `npm ci` with lockfiles; the npm package is packed and installed in an empty project in the CI.
- End-to-end tests over a real Kestrel and SignalR server, driven by the client from Node.
- The web samples and the TypeScript client open in Visual Studio (one `.esproj` each), with shared launch profiles that start the server and the apps (`Routeables.SignalR.slnLaunch`). Outside Visual Studio they run neither `npm install` nor their build.
- The package is marked not trimmable; `AddRouteablesSignalR` carries `[RequiresUnreferencedCode]` and `[RequiresDynamicCode]`.

### Breaking changes

- Properties are no longer sent unless exposed (`[RouteableState]`), and no longer settable unless `Settable`.
- `RouteableSession`: `Watch`, `Unwatch`, `Set`, `SetOn`, `Answer`, `Dismiss` are now `WatchAsync`, `UnwatchAsync`, `SetAsync`, `SetOnAsync`, `AnswerAsync`, `DismissAsync`; `MaxTargets` moved to `RouteablesOptions`.
- `RouteableStateProjector` and `RouteableCommandInvoker` are classes built on a `RouteableSchema` (they were static).
- The messages: `RouteableChanged` carries patches, `Navigated` a generation and an instance, `IRouteableClient` has `Completed`, the hub has `Hello` and `Resync`. Protocol 2: update `@routeables/client` with the server.
- `@routeables/client`: `RouteableTransport.call` is generic and rejects when the call does not reach the server; `ServerMessages` has `completed`; the pages are no longer cleared when the connection drops.
