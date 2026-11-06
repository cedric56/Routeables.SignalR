# Routeables.SignalR

Serves the view models of a Routeables application to a browser front end (Angular, Vue or any JavaScript client) over SignalR. The view models stay on the server, as with Blazor Server: the browser shows their state and calls their commands.

```csharp
builder.Services.AddRouteablesSignalR(services =>
    services.RegisterRoute<CounterView, CounterViewModel>(route: "/counter"));   // or AddGeneratedRoutes()

app.MapRouteables();   // the hub, at /routeables
```

```csharp
public sealed class CounterViewModel : INotifyPropertyChanged
{
    public int Count { get; private set; }          // sent, and sent again at every PropertyChanged

    [RouteableCommand]                               // only these methods can be called from the browser
    public void Increment() => Count++;
}
```

## What a connection owns

One `RouteableSession` per connection: a router with its history, and the view models it creates. When the connection closes the session is disposed, with the view models. After a reconnection the client has a new session: the application goes to its route again.

## Targets

The client does not only see one page. What the server shows is a set of **targets**:

| Target | What it shows | When it is sent |
| --- | --- | --- |
| `""` | The page of the main router. | Always. |
| `$modal` | The view model of the modal that is open (see below). | Always, while it is open. |
| a region name (`"history"`) | The page the router of that region shows (`AddRegion("history")`). | After `Watch(region)`. |
| `$name` | A view model registered with `AddRouteableTarget<T>("$name")` (a menu, the state of a shell). | After `Watch("$name")`. |

A client can watch at most `RouteableSession.MaxTargets` targets. `GoToIn(region, route)` navigates the router of a region.

## The messages

They do not depend on SignalR (`IRouteableClient`, `RouteableNavigated`, `RouteableChanged`...), so that another transport can carry them.

| Client to server | Does |
| --- | --- |
| `GoTo(route)` / `GoToIn(target, route)` | Goes to a route, as an address bar would; in a region for `GoToIn`. |
| `Invoke(command, arguments)` / `InvokeOn(target, command, arguments)` | Calls a command (below). SignalR has no optional parameters: always send `arguments`, `[]` when there are none. |
| `Set(property, value)` / `SetOn(target, property, value)` | Sets a public property with a setter (a text box, a check box). A type that converts from a string (`RouteTransition`) is set with a string. |
| `Watch(target)` / `Unwatch(target)` | Starts or stops the messages of a region or a `$name` target. |
| `Answer(id, button)` | Answers a dialog with the index of a button. |
| `Dismiss(id, click)` | A notification was closed, or clicked. |

| Server to client | Tells |
| --- | --- |
| `Navigated` | A page is current in a target: route, view model name, state, commands. Also when a command navigates. |
| `Changed` | A property of a target changed. It is sent for the objects inside the view model too: an `INotifyPropertyChanged` or `INotifyCollectionChanged` it holds, and the `CanExecuteChanged` of a command. |
| `Closed` | A target has no view model any more (the modal closed). |
| `DialogRequested` | A message, content or task dialog to answer (see below). |
| `Notified` | A notification to show. |
| `Failed` | A route change, or a command that went on after its call returned, failed. |

## Commands

A client calls a command in one of two ways:

- a method that carries `[RouteableCommand]`, by its name;
- an `ICommand` of the state, by its path. The property is sent as `{ "$command": "choices.2.adoptCommand", "canExecute": true }`; the client gives the path back (`InvokeOn(target, path, [])`). A command that cannot execute is refused. A command with an `ExecuteAsync()` method (the `AsyncCommand` of the samples) is awaited, so its failure reaches the client.

A call returns when the command ends, when it opens a dialog (it does not end before the user answers), or after a moment (a countdown goes on by itself).

## Dialogs, notifications and the modal

`AddRouteablesSignalR` registers `RemoteDialogService` and `RemoteNotificationService`: the dialogs and the notifications of a view model go to the browser, and the view model awaits the answer like it awaits a window on a desktop. A modal view model (`ICloseableViewModel<T>`) needs a view that derives from `RemoteModal<TViewModel, TResult>`:

```csharp
[RouteableView(typeof(NoteViewModel))]
public sealed class NoteModal(RemoteInteractions interactions) : RemoteModal<NoteViewModel, string?>(interactions);
```

The client receives the view model as the page of the target `$modal`, calls its commands there, and gets `Closed` when it closes.

## What leaves the server

- **State:** the public readable properties, by their camel case name. A delegate, a task, a type, a stream, a service (`IRouter`, `IServiceProvider`) and a property marked `[JsonIgnore]` stay on the server. Values are scalars, lists and dictionaries; objects are sent four levels deep, and a cycle is cut. A command (`ICommand`) is sent as a token (above).
- **Commands:** only methods with `[RouteableCommand]`, and the `ICommand` properties of the state. Arguments are read from JSON in order; a parameter with a default value may be left out.
- **`GoTo` accepts any route** that is registered, and `GoToIn` any region: put the guards (`IRouteGuard`, authorization) on the routes that need them.

## Limits

- The projection reads types by reflection: not for trimmed or AOT applications.
- A page that has no `INotifyPropertyChanged` is sent once: nothing tells it changed.
- A connection that drops loses its view models. Several servers need the same server for a client (sticky sessions) or a SignalR backplane.
- A notification of the operating system (`CreateNotificationAsync`) is not offered: the browser draws the notifications.

The client is in [clients/typescript](../../clients/typescript). The sample is [samples/SignalR/CounterServer](../../samples/SignalR/CounterServer).
