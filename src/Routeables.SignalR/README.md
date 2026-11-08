# Routeables.SignalR

Serves the view models of a Routeables application to a browser front end (Angular, Vue or any JavaScript client) over SignalR. The view models stay on the server, as with Blazor Server: the browser shows their state and calls their commands.

```csharp
builder.Services.AddRouteablesSignalR(services =>
    services.RegisterRoute<CounterView, CounterViewModel>(route: "/counter"));   // or AddGeneratedRoutes()

app.UseAuthentication();
app.UseAuthorization();
app.MapRouteables().RequireAuthorization();   // the hub, at /routeables
```

```csharp
public sealed class CounterViewModel : INotifyPropertyChanged
{
    [RouteableState] public int Count { get; private set; }               // sent, and sent again at every PropertyChanged
    [RouteableState(Settable = true)] public int Step { get; set; } = 1;  // sent, and set by the browser (a text box)
    public string InternalNote { get; set; } = "";                        // never leaves the server

    [RouteableCommand]                                                    // only these methods can be called from the browser
    public void Increment() => Count += Step;

    [RouteableCommand, Authorize(Roles = "manager")]                      // and only by the users who may
    public void Reset() => Count = 0;
}
```

## What crosses the network

Nothing that a view model does not expose. By default (`RouteableExposure.Explicit`):

| What | Sent to the browser | Changed by the browser |
| --- | --- | --- |
| A property marked `[RouteableState]` | yes | no |
| A property marked `[RouteableState(Settable = true)]` with a public setter | yes | `Set` / `SetOn` |
| Every public property of a class marked `[RouteableState]` (or `[RouteableState(Settable = true)]`) | yes | when the class says `Settable`, unless the property says `[RouteableState(Settable = false)]` |
| A type accepted by `options.ExposeTypes(predicate, settable)` (view models of a library you cannot annotate) | yes | when `settable` |
| Any other property | no | no |
| A method marked `[RouteableCommand]` | its name | `Invoke` / `InvokeOn` |
| An `ICommand` held by an exposed property | `{ "$command": "items.2.buyCommand", "canExecute": true }` | `InvokeOn(target, path, [])` |

`[JsonIgnore]` keeps a property on the server in every mode. A delegate, a task, a type, a stream, a `ClaimsPrincipal` and a service (`IRouter`, `IServiceProvider`) are never sent. The `RouteableCommand` buttons of a Routeables view model (a name and a command) are exposed, read-only.

`options.Exposure = RouteableExposure.Public` restores the behaviour of the first versions (every public property read, every public setter callable): a deliberate choice for a prototype, not for view models that hold more than the screen.

## Authorization

Three barriers, evaluated with the authorization of ASP.NET Core (`IAuthorizationService`, your policies) against the user of the connection. What the user **does** (a command, a property set, a route change) is authorized at every call, so a policy that reads current permissions sees them change during a session; what the user **sees** is authorized when it is shown (see below):

- **The hub.** Protect the endpoint as any other: `app.MapRouteables().RequireAuthorization()`. `options.RequireAuthenticatedUser = true` also closes an anonymous connection that would reach it.
- **The view model.** `[Authorize]` on the class: the route guard that `AddRouteablesSignalR` registers cancels a route change to it, whoever asked for it (the client or a command), and a view model the user may not see is never sent, whatever showed it (a region, a registered target). A registered target (`AddRouteableTarget`) follows the same rule.
- **The member.** `[Authorize]` on a `[RouteableCommand]` method; `[RouteableAuthorize]` on a property (ASP.NET Core's attribute cannot go on a property): the property is neither sent nor set, and the command it holds not called, for a user who does not meet it. `[AllowAnonymous]` lets anybody through, as in ASP.NET Core. The commands a user may not call are left out of the list the page sends.
- **The objects inside a state.** The same attributes apply at any depth: on a nested type (`[Authorize]` on the class) or on one of its properties (`[RouteableAuthorize]`), a property is not sent to a user who does not meet them — in the page, in a change, in an item added to a list — and a command it holds cannot be called. A nested object the user may not read at all is sent empty. The decision is taken once per object and per page (the policy receives the object as its resource); an object met for the first time in a change is sent without what is not decided yet, then again once it is.

**When a permission changes.** What is shown was authorized when it was shown. Where the application withdraws or grants a permission, it asks the sessions to authorize it again: they send their targets again, without what may not be read any more (and with what may now be):

```csharp
await sessions.ReauthorizeAsync(user => user?.FindFirstValue(ClaimTypes.NameIdentifier) == userId);   // RouteableSessions, a singleton
```

The user of a connection is the one that opened it, as everywhere in SignalR: `MapRouteables(options: o => o.CloseOnAuthenticationExpiration = true)` closes it when its cookie or its token expires, and the client resumes its session with a new one.

When a member asks for authorization and no authorization service is registered, it is refused: an absent service never opens a door. `AddRouteablesSignalR` registers `AddAuthorizationCore()`; add your policies as usual.

`RouteableConnection` (scoped) gives a view model or a service the user of the connection.

## What a connection owns

One `RouteableSession` per connection: a router with its history, and the view models it creates.

**Its dispatcher.** Every call of the client and every change of a view model runs on the dispatcher of the session, one at a time, in order, as on the UI thread of a desktop application. An `await` in a view model comes back to it; work may interleave at an `await`, never in between. A view model is never used by two threads at once, and a `PropertyChanged` raised on another thread is brought to the dispatcher before it is told.

**Its stream of messages.** Every target is an ordered stream: `Navigated` (a full state, a new *generation*), then `Changed` (the same generation, *version* 1, 2, …). The client drops a message of an older generation and asks the state again (`Resync`) when a version is missing. The answer to a call is written after the messages the call caused: a client that gets the answer already has the state that goes with it.

**Changes are patches.** A change says what changed: `set` a value at a path (`["title"]`, `["lines", 3, "text"]`), or `splice` an array (an item added, removed, moved or replaced in an `ObservableCollection`). The changes of one turn of the dispatcher are sent together, once: a property set a thousand times in a loop is sent once, with its last value. While a slow client lets messages wait, the changes of a target are merged into the one that waits; beyond `MaxPendingMessages` the connection is closed and the client resumes its session.

**What blocks it.** The dispatcher is the UI thread of the session: work that blocks it (`.Result`, `.Wait()`, a synchronous query, a long loop) delays every other call and change of that session — not of the others. A synchronous command runs on the thread of the call until its first `await`, so `CommandGrace` does not shorten it: make long work asynchronous, or move it to a service (`await Task.Run(...)`), and come back to the dispatcher to change the view model. `SynchronizationContext.Send` from another thread waits for the dispatcher, as it waits for a UI thread.

**Calls that go on.** A command or a route change that lasts longer than `CommandGrace` (800 ms), or that asks the visitor something (a dialog, a modal, a page that asks before it is left), is answered `pending` with an operation id; `Completed` tells its end, with its success or its failure. SignalR runs the calls of a client one after the other: this is what lets the answer to a dialog reach the server while the command or the route change that asked it waits.

**Its reconnection.** When the connection drops, the session waits `ReconnectGracePeriod` (30 s) for its client. The client says `Hello` with the token of its session on the new connection: the session is resumed — its view models, the state of every target (sent again), the dialogs that wait, a notification told while it was away, and the end of each call the client still waits for (it names them in `Hello`): a `Completed` told while the connection was gone, or lost with it, is told again after the state. The session keeps the end of its last 256 calls; the end of an older one is told as `lost` (the call may have run). Only the user the session was created for may resume it (the same scheme, issuer and identifier; a user authenticated without an identifier cannot resume). A client that comes back before the server noticed that its old connection dropped gets its session too: the old connection is closed. Only a session whose client said `Hello` waits; at most `MaxDetachedSessions` (10 000) wait at once on a server, and `MaxDetachedSessionsPerUser` (16) of one user: beyond, the one that waits for the longest is disposed. After the grace period the session is disposed, and the client starts a new one (`@routeables/client` opens the last route again).

## Targets

| Target | What it shows | When it is sent |
| --- | --- | --- |
| `""` | The page of the main router. | Always. |
| `$modal` | The view model of the modal that is open (see below). | Always, while it is open. |
| a region name (`"history"`) | The page the router of that region shows (`AddRegion("history")`). | After `Watch(region)`. |
| `$name` | A view model registered with `AddRouteableTarget<T>("$name")` (a menu, the state of a shell). | After `Watch("$name")`. |

A client can watch at most `MaxTargets` targets (24) besides the page and the modal. `GoToIn(region, route)` navigates the router of a region.

## The messages

They do not depend on SignalR (`IRouteableClient`, `RouteableNavigated`, `RouteableChanged`…), so that another transport can carry them. The protocol has a version (`RouteableProtocol.Version`, 2), checked by `Hello`.

| Client to server | Does |
| --- | --- |
| `Hello({ protocol, resume, waiting })` | First, and after each reconnection: the protocol of the client, the token of the session to resume, and the operations of the calls it still waits for. Answers `{ success, protocol, session, resumed }`. |
| `GoTo(route)` / `GoToIn(target, route)` | Goes to a route, as an address bar would; in a region for `GoToIn`. |
| `Invoke(command, arguments)` / `InvokeOn(target, command, arguments)` | Calls a command. SignalR has no optional parameters: always send `arguments`, `[]` when there are none. |
| `Set(property, value)` / `SetOn(target, property, value)` | Sets a settable property. A type that converts from a string (`RouteTransition`) is set with a string. |
| `Watch(target)` / `Unwatch(target)` | Starts or stops the messages of a region or a `$name` target. |
| `Resync(target)` | Sends the full state of a target again. |
| `Answer(id, button)` | Answers a dialog with the index of a button. |
| `Dismiss(id, click)` | A notification was closed, or clicked. |

Every call answers `{ success, error, code, operation, pending }`. The codes: `not-found`, `unauthorized`, `invalid-argument`, `failed`, `cancelled`, `limit`, `disabled`, `protocol`.

| Server to client | Tells |
| --- | --- |
| `Navigated` | A page is current in a target (or its state is sent again): route, view model name, state, commands, generation, instance. |
| `Changed` | Patches of the state of a target: target, generation, version, patches. |
| `Closed` | A target has no view model any more (the modal closed, a page the user may not see). |
| `Completed` | A call answered `pending` has ended. |
| `DialogRequested` | A message, content or task dialog to answer (see below). |
| `Notified` | A notification to show. |
| `Failed` | Something failed that no call waits for: a route change, a part of a state that could not be sent. |

## Commands

A client calls a command in one of two ways:

- a method that carries `[RouteableCommand]`, by its name. It returns `void`, `Task`, `Task<T>`, `ValueTask` or `ValueTask<T>` (the value is not sent); its parameters are read from the JSON the client sends, in order, and a parameter with a default value may be left out. A `CancellationToken` parameter is not sent by the client: it is cancelled when the session ends;
- an `ICommand` of the exposed state, by its path. A command that cannot execute is refused (`disabled`). A command with an `ExecuteAsync` method (`AsyncRelayCommand`, the `AsyncCommand` of the samples) is awaited, so its failure reaches the client. An `ICommand` of your own that starts asynchronous work from `Execute` without exposing `ExecuteAsync` cannot be awaited: the call ends when `Execute` returns, and its failure is not seen.

Two commands of one type with the same name (`[RouteableCommand(Name = "delete")]` on two methods, or two overloads) are refused when the type is first read (`InvalidOperationException`): which one a client calls must never depend on the order of reflection. A method hidden by `new` in a derived class is replaced by it, as a property is.

What a client sends is checked before anything reads it: `MaxArgumentLength` (64 KiB of JSON), `MaxArgumentItems` (10 000 values), `MaxArgumentDepth` (32), at most 32 arguments, names of at most 512 characters. Every failure — a wrong type, an exception of the command, before or after its first `await`, a cancellation — is an answer, never an exception of the hub.

An exception of a view model reaches the client with its message in Development (or when `DetailedErrors = true`); elsewhere the client reads "The command failed." and the exception is logged. Throw a `RouteableException` for a message meant for the visitor: it is always sent.

## Dialogs, notifications and the modal

`AddRouteablesSignalR` registers `RemoteDialogService` and `RemoteNotificationService`: the dialogs and the notifications of a view model go to the browser, and the view model awaits the answer like it awaits a window on a desktop. A modal view model (`ICloseableViewModel<T>`) needs a view that derives from `RemoteModal<TViewModel, TResult>`:

```csharp
[RouteableView(typeof(NoteViewModel))]
public sealed class NoteModal(RemoteInteractions interactions) : RemoteModal<NoteViewModel, string?>(interactions);
```

The client receives the view model as the page of the target `$modal`, calls its commands there, and gets `Closed` when it closes. A modal opened from a modal covers it: when it closes, the one under it is sent again as the page of `$modal`, still awaited by the command that opened it. A dialog is sent after the changes that were made before it was asked; a dialog without buttons is answered with 0 only. A notification stays open on the server until the client dismisses it (the clients do it after its expiration); a session keeps the last 64 that were not dismissed.

## Limits of a state

A collection of more than `MaxCollectionItems` (10 000) items, or a property that holds more than `MaxStateNodes` (100 000) values, is sent as `null` and the client is told why (`Failed`, code `limit`): page it. Objects are read `MaxDepth` (4) levels deep, and a cycle is cut. A getter or an enumerator that throws gives `null` for that property (or leaves it out), the others are sent, and the client is told (`Failed`): with the message of the exception in Development only, as for a command, and the exception is logged. A collection changed by another thread while it is read is read again.

**What is read again.** A property is read again whenever it changes (`PropertyChanged`), whole; an item of a list when it changes, and the list when it changes (a splice), except a list whose items hold commands, which is sent whole. A lazy `IEnumerable` or an `IQueryable` exposed as a property is enumerated at every reading — a query to the database at every change of that property: expose a list that the view model fills instead.

## Options

```csharp
builder.Services.AddRouteablesSignalR(services => ..., options =>
{
    options.Exposure = RouteableExposure.Explicit;                 // the default
    options.ExposeTypes(t => t.Namespace == "MyLib.ViewModels", settable: true);
    options.RequireAuthenticatedUser = true;
    options.CommandGrace = TimeSpan.FromMilliseconds(800);
    options.ReconnectGracePeriod = TimeSpan.FromSeconds(30);
    options.MaxTargets = 24;
    options.MaxCollectionItems = 10_000;
    options.MaxStateNodes = 100_000;
    options.MaxPendingMessages = 1_000;
    options.MaxDetachedSessions = 10_000;
    options.MaxDetachedSessionsPerUser = 16;
    options.MaxArgumentLength = 64 * 1024;
    options.DetailedErrors = null;                                 // Development only
});
```

The options are checked when the hub is registered: a limit that is not positive, a negative or infinite duration, a `MaxCollectionItems` larger than `MaxStateNodes` throw an `InvalidOperationException` that lists every problem (`RouteablesOptions.Problems()`), so the application does not start with them.

## Observability

The logs of the sessions (`Routeables.SignalR.RouteableSession`), and an activity source and a meter named `Routeables.SignalR` (`RouteablesTelemetry.Name`): every call is an activity; the metrics count the sessions (attached and waiting for their client), the calls by method and outcome and their duration, the messages sent, merged and dropped, the depth of the queue of a slow client, the delivery latency, and the parts of a state that could not be sent.

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(RouteablesTelemetry.Name))
    .WithMetrics(m => m.AddMeter(RouteablesTelemetry.Name));
```

## Deployment

- **One server per client.** A session lives in the memory of the server that holds its connection: with several servers, the client must always reach the same one (sticky sessions; with WebSockets and `skipNegotiation` a connection stays on its server anyway). A SignalR backplane is **not** needed: a session only talks to its own connection. A session cannot move to another server, and a restart loses them: the clients start new sessions and open their route again. Keep in the view models nothing that must outlive a session; keep it in your services and your database.
- **No trimming, no Native AOT.** The view models are read by reflection (cached per type): `AddRouteablesSignalR` carries `[RequiresUnreferencedCode]` and `[RequiresDynamicCode]`, and the package is marked not trimmable.
- **Messages from the browser.** SignalR limits a message to 32 KiB by default (`HubOptions.MaximumReceiveMessageSize`); the limits above apply within it. Rate limiting and the expiration of the authentication: see [SECURITY.md](../../SECURITY.md).
- **What "sent" means.** A call is answered once the messages it caused were written to the connection, not once the client applied them. The client detects what it did not receive (a missing version, a call it still waits for) and asks for it again; nothing else is acknowledged.

The client is in [clients/typescript](../../clients/typescript). The samples are [samples/Angular](../../samples/Angular) (with its server, XAnimals.AngularServer) and [samples/Vue](../../samples/Vue). Security: [SECURITY.md](../../SECURITY.md).
