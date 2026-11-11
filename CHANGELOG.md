# Changelog

## 2.0.0 — unreleased

The first version meant for production. It changes the protocol (2) and some defaults: see "Breaking changes".

### The device of each user: Essentials

- **`Routeables.SignalR.Essentials`**, a new package: `services.AddRouteablesEssentials()` gives the view models every .NET MAUI
  Essentials contract (36, from `IAccelerometer` to `IWebAuthenticator`) of the client of their session, and
  `app.MapRouteablesEssentials()` carries the files of the pickers, the screenshot, `Share`, `Launcher` and `Email` (upload slots and
  download links bound to the session, never rendered on the origin of the app). The synchronous members answer from the state the
  client announced (no round trip); the writes go to the client in order and survive a reconnection. `Preferences.Default` and
  `Connectivity.Current` forward to the session that runs (`RouteableSession.Running`) and throw outside of one: never the service of
  another user.
- **`RouteableEssentials`** in Routeables.SignalR: the Essentials messages (`EssentialsReady`, `EssentialsInvoke`, `EssentialsResult`,
  `EssentialsSubscribe`, `EssentialsEvent`), with timeouts, replay after a reconnection, validation of what the client sends, and the
  options `EssentialsTimeout` and `EssentialsMaxPendingCalls`. `IRouteableClient` has two more methods, with a default implementation.
- **`@routeables/client`**: the `essentials` option, `browserEssentials()` (`localStorage`, `navigator.onLine`), and `EssentialsBridge`
  that only runs the methods and events a service lists.

### Offline data: the Routeables Remote Protocol

- **`Routeables.Remote`**, a new package without dependency (Native AOT): documents replicated between a server and clients that
  write offline (last-writer-wins fields, add-wins sets, counters, collaborative text, deletion), with an exactly-once push, a
  one-cursor pull, snapshots after compaction, and the rules of the server (`IReplicationPolicy`) over every change and every
  read. See [PROTOCOL-REMOTE.md](PROTOCOL-REMOTE.md).
- **`ReplicationHub`**: `services.AddRouteablesReplication(...)` and `app.MapRouteablesReplication()` serve it over SignalR, and
  tell the connected clients when the log of a collection grows (`LogAdvanced`).
- **Clients**: `Replica` in `@routeables/client` (TypeScript, with `signalRReplicationChannel`) and in `routeables_client` (Dart,
  with `SignalRReplicationChannel`). `clients/fixtures/remote-v1.json`, written by the .NET implementation, checks that the three
  merge alike.
- **A durable server**: `IReplicationStore`; `Queryeables.Replication` stores the protocol in EF Core, MongoDB, Dommel or
  PostgreSQL and writes the merged documents to the application's entities.

### Security

- **Explicit exposure by default.** Only properties marked `[RouteableState]` (or classes marked with it, or types accepted by `RouteablesOptions.ExposeTypes`) are sent; only those marked `[RouteableState(Settable = true)]` can be set by the client. Before, every public property was sent and every public setter could be called. `RouteableExposure.Public` restores that, as a deliberate choice.
- **Authorization of commands, properties and pages**, at every call, with ASP.NET Core's authorization: `[Authorize]` / `[AllowAnonymous]` on a `[RouteableCommand]` method or a view model class, `[RouteableAuthorize]` on a property. A route guard (`RouteableAuthorizationGuard`) cancels a route change to a view model the user may not see, and such a view model is never sent. The commands a user may not call are left out of the page.
- **Authorization of the objects inside a state**: `[Authorize]` on a nested type and `[RouteableAuthorize]` on its properties apply to what is sent (the page, its changes, the items added to a list), as they applied to the commands at the same path. Before, only the properties of the view model itself were filtered: a protected property of a nested object was sent to every user.
- An `ICommand` is reachable only through an exposed property: it was reachable through any public property.
- **What a getter throws is no longer sent outside Development.** A getter or an enumerator of the state that throws is reported to the client with a generic message (its own message in Development, or a `RouteableException`'s), and logged. Before, its message (a connection string, a path) reached the browser whatever `DetailedErrors` said.
- **Two commands with one name are refused** when the type is read: which one a client called depended on the order of reflection, and could be the less protected one. A method hidden by `new` is replaced by it.
- **Who may resume a session**: the scheme, the issuer and the identifier of the user (`NameIdentifier`, else `sub`). A user authenticated without an identifier can no longer resume a session (all such users shared one key).
- **Sessions that wait are bounded**: only a session whose client said `Hello` waits for it, at most `MaxDetachedSessions` (10 000) per server and `MaxDetachedSessionsPerUser` (16). Before, opening and dropping connections in a loop kept a session alive 30 s each, without limit.
- **Authorizing what is shown again**: `RouteableSessions.ReauthorizeAsync(user => ...)` and `RouteableSession.ReauthorizeAsync()` send the targets again with what the user may read now. SECURITY.md says precisely when reading is authorized (when shown) and calling (at every call). `MapRouteables(options: ...)` takes the options of the connections (`CloseOnAuthenticationExpiration`).
- `[RouteableState(Settable = false)]` on a property wins over `[RouteableState(Settable = true)]` on its class.
- A dialog without buttons accepts the answer 0 only; a session keeps the last 64 notifications that were not dismissed.
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
- **A client that comes back before the server noticed it left** gets its session: the old connection is closed. Before, a new session was made, and the old one waited for nobody. Every move of a session (connected, detached, resumed, expired) is made under one lock.
- **Modals opened from a modal**: the one under is shown again when the one on top closes. Before, it disappeared from the client while its command still awaited it.
- A message that cannot be serialized is logged as an error; a message lost with its connection is counted by reason (`closed`, `timeout`, `transport`). New metrics: `routeables.signalr.overflows`, `routeables.signalr.sessions.evicted`.
- The reflection of `ExecuteAsync` and `ValueTask<T>.AsTask` is cached per type.
- **The end of a call is never lost**: the client names in `Hello` the calls it still waits for, and the resumed session tells their end again after the state — a `Completed` told while the connection was gone, or lost with it. Before, it was dropped with the oldest messages beyond 256, or lost when the connection failed as it was written, and the call never resolved. The session keeps the end of its last 256 calls; an older one ends as `lost`.
- The options are checked when the hub is registered (`RouteablesOptions.Problems()`): a limit that is not positive, a negative duration or contradictory limits stop the application at startup.
- Logs, an activity per call, and metrics (`RouteablesTelemetry`, meter and source `Routeables.SignalR`).

### Client (`@routeables/client`)

- Applies patches by generation and version, asks the state again when a change is missing.
- Resolves a call when it ends (`Completed`), even when the end arrives before the answer.
- Removes a dialog or a notification once the server took the answer; keeps it when the answer did not reach the server.
- `onError` for calls that did not reach the server and watches that were refused.
- Keeps the pages while reconnecting, resumes the session, and recovers a lost one (`recover`, `onSessionLost`).
- Names the calls it waits for when it resumes its session, so that their end is never lost.
- The calls that wait end with `{ code: 'disconnected' }` after `disconnect()`; they waited forever. After SignalR stopped retrying, they wait for `connect()`, which resumes the session (they end then) or finds it lost (`session-lost`).
- Closes a notification after its expiration (`expireToasts`, on by default): the samples no longer do it themselves.
- `exports` in `package.json`: the public API is the root of the package.

### Client for Dart and Flutter (`routeables_client`)

- A port of `@routeables/client` to Dart, on the SignalR client for Dart (signalr_netcore): the same protocol, the same guarantees, the same tests (with the changes of the TypeScript client above). Not published on pub.dev yet.
- A Flutter sample (web, desktop, mobile) against the sample server, which allows the pages of the machine (CORS) for Flutter web.

### Build and distribution

- Builds against the current Routeables (`GetEntries` / `IMenuEntry`); the CI pins the revision of Routeables (`ROUTEABLES_REF`).
- Depends on the Routeables package (2.1.0) instead of its sources. The XAnimals view models of the samples are compiled against the same package (`samples/XAnimals`), and the generator of routes of Routeables runs only in `XAnimals.ViewModels`, as in Routeables.
- The CounterServer sample is removed: the web samples show the library. The server of the samples is `samples/Server` (XAnimals.Server), shared by Angular, Vue, React and Flutter; it was `samples/Angular/XAnimals.AngularServer`.
- A React web sample, beside Angular and Vue: the same application against the same server, each built and driven in Chrome by the CI (`tests/samples/smoke.mjs`).
- `-p:RouteablesVersion=…` builds and packs against the Routeables packages; a release workflow publishes the NuGet and npm packages from a tag, after installing the NuGet package in an empty application.
- `npm ci` with lockfiles; the npm package is packed and installed in an empty project in the CI.
- End-to-end tests over a real Kestrel and SignalR server, driven by the client from Node.
- The web samples and the TypeScript client open in Visual Studio (one `.esproj` each), with shared launch profiles that start the server and the apps (`Routeables.SignalR.slnLaunch`). Outside Visual Studio they run neither `npm install` nor their build.
- The package is marked not trimmable; `AddRouteablesSignalR` carries `[RequiresUnreferencedCode]` and `[RequiresDynamicCode]`.
- The release runs the tests of the server against the published packages of Routeables before it publishes, and refuses a tag that does not match `VersionPrefix` and `package.json`.
- The Angular sample is driven in Chrome by the CI too; Flutter is pinned to a release line (3.47.x); Dependabot keeps the actions and the packages up to date. `global.json` no longer declares `Uno.Sdk`; an empty `package-lock.json` is removed.

### Breaking changes

- Properties are no longer sent unless exposed (`[RouteableState]`), and no longer settable unless `Settable`.
- `RouteableSession`: `Watch`, `Unwatch`, `Set`, `SetOn`, `Answer`, `Dismiss` are now `WatchAsync`, `UnwatchAsync`, `SetAsync`, `SetOnAsync`, `AnswerAsync`, `DismissAsync`; `MaxTargets` moved to `RouteablesOptions`.
- `RouteableStateProjector` and `RouteableCommandInvoker` are classes built on a `RouteableSchema` (they were static).
- The messages: `RouteableChanged` carries patches, `Navigated` a generation and an instance, `IRouteableClient` has `Completed`, the hub has `Hello` and `Resync`. Protocol 2: update `@routeables/client` with the server.
- `@routeables/client`: `RouteableTransport.call` is generic and rejects when the call does not reach the server; `ServerMessages` has `completed`; the pages are no longer cleared when the connection drops.
- `RemoteInteractions.ModalClosed` gives the view model that closed; `RouteableProjectionIssue` has the `Exception`; a session whose client never said `Hello` is disposed when its connection drops.
