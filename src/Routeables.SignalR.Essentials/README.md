# Routeables.SignalR.Essentials

The .NET MAUI Essentials of each user, for the view models of a Routeables.SignalR server. A view model keeps asking for
`IPreferences` or `IConnectivity`, or reads `Preferences.Default`, as it does in a MAUI, Avalonia or Blazor app; on the server, the
call reaches the browser (or the app) of the user of the session, never another one.

```csharp
builder.Services.AddRouteablesSignalR(services =>
{
    services.AddRouteablesEssentials();   // every MAUI Essentials contract, and the static facades
});

var app = builder.Build();
app.MapRouteables();
app.MapRouteablesEssentials();   // the files of the pickers, the screenshot, Share, Launcher and Email
```

```ts
import { RouteablesClient, browserEssentials, signalRTransport } from '@routeables/client';

const client = new RouteablesClient(signalRTransport('/routeables'), { essentials: browserEssentials() });
```

The view models only reference `Microsoft.Maui.Essentials`:

```csharp
public sealed class SettingsViewModel(IPreferences preferences, IConnectivity connectivity)
{
    [RouteableState] public string Theme => preferences.Get("theme", "system");
    [RouteableState] public bool Offline => connectivity.NetworkAccess == NetworkAccess.None;
}
```

## What runs where

Every contract of `Microsoft.Maui.Essentials` is registered, one instance per session. "State" members are answered on the server from what
the client announced (no round trip); "call" members go to the client; "post" members are sent without waiting (they are synchronous in MAUI).

| Contract | Server | Browser (`browserEssentials()`) |
| --- | --- | --- |
| `IPreferences` | state + writes in order, replayed | `localStorage` |
| `ISecureStorage` | keys in the state, values by call | AES-GCM, non-extractable key in IndexedDB |
| `IConnectivity`, `IBattery`, `IDeviceDisplay` | state + `changed` events | `navigator.onLine`, Battery API, `resize`, Wake Lock |
| `IAppInfo`, `IDeviceInfo` | state | options of the app, `userAgentData`, `prefers-color-scheme` |
| `IVersionTracking` | computed on the server, as MAUI does, in the client's preferences | — |
| `IGeolocation` | calls; listening survives a reconnection | `navigator.geolocation` |
| `IGeocoding` | calls | a Nominatim instance of yours (`nominatim` option) |
| `IAccelerometer`, `IGyroscope`, `ICompass`, `IOrientationSensor`, `IMagnetometer`, `IBarometer` | `start`/`stop` posts, readings as events (at most 20 per second); shake detected on the server | `devicemotion`, `deviceorientation`, Generic Sensor API |
| `IVibration`, `IHapticFeedback`, `ISemanticScreenReader`, `IPhoneDialer` | posts | `navigator.vibrate`, `aria-live`, `tel:` |
| `IFlashlight`, `IClipboard`, `ITextToSpeech` | calls | torch constraint, Clipboard API, `speechSynthesis` |
| `IBrowser`, `ILauncher`, `IMap` | calls; the schemes that run code are refused on the server | new tab without `opener`, OpenStreetMap |
| `IEmail`, `ISms` | calls | `mailto:`, `sms:` (no attachment) |
| `IContacts` | calls | Contact Picker API (Chrome on Android) |
| `IShare` | calls; files as links | Web Share API |
| `IFilePicker`, `IMediaPicker`, `IScreenshot` | calls; files uploaded to the session | `<input type="file">` (`capture` on phones), `getDisplayMedia` |
| `IWebAuthenticator` | calls; the callback URL is checked on the server | popup, callback on the origin of the app |
| `IFileSystem` | on the server: directories of the session, web root as the app package | — |
| `IAppActions` | calls | none (`IsSupported` is false) |

A service the client did not announce makes its members throw `FeatureNotSupportedException` (a `bool IsSupported` is false). A call
that waits for the user (a permission, a dialog, the camera) has no timeout; it ends with the call, the session, or its token.

## Files

A file never crosses SignalR. `MapRouteablesEssentials()` maps `POST` and `GET {Path}/files/{token}`:

- **From the client**: a pick opens an upload slot for as many files as it may bring; the client sends each file there (the name in
  `X-File-Name`) and answers with the ids the server gave. The files are in a directory of the session, deleted with it
  (`RemoteFileResult`: `FullPath` is a real file; read it with `RemoteFileResult.OpenReadAsync()` or `File.OpenRead`, the portable MAUI
  build cannot). A slot closes with the call; it takes at most `MaxFilesPerPick` files of at most `MaxFileSize` bytes.
- **To the client**: a file a view model shares, opens or attaches gets a link valid `FileLinkLifetime`. It is always served as a download
  (`application/octet-stream`, `Content-Disposition: attachment`, `nosniff`, `Content-Security-Policy: sandbox`): an HTML or SVG file
  never runs on the origin of the app. The client receives its real type in the call.
- The tokens are random, single-purpose, and bound to the user of the session when it is authenticated: another user, or nobody, gets
  `404`. Protect the group like the hub: `app.MapRouteablesEssentials().RequireAuthorization()`.

## The facades

`Preferences.Default` and `Connectivity.Current` are process-wide, so they never hold the service of a user. `AddRouteablesEssentials`
installs, once, facades that forward every call to the service of the session that runs (`RouteableSession.Running`): the one the view
models of that session receive by injection. A session runs on its dispatcher (a command, a property set, an event of a view model, an
`await` that came back) and in what that work starts (`Task.Run`, `ConfigureAwait(false)`). Outside of a session (a hosted service, a
timer created at startup) the facades throw `InvalidOperationException`: there is no user to ask.

`AddRouteablesEssentials(installFacades: false)` leaves the facades alone, for a process where another host owns them (a Blazor Server
app using Essentieables); injection keeps working.

## The protocol

`RouteableEssentials`, in Routeables.SignalR, carries it; it does not know MAUI. Version 1:

| Direction | Message | |
| --- | --- | --- |
| client → server | `EssentialsReady { version, capabilities, state }` | the services the client implements, and their state |
| server → client | `EssentialsInvoke { id, service, method, args }` | a call |
| client → server | `EssentialsResult { id, success, result?, error?, code? }` | its answer |
| server → client | `EssentialsSubscribe { service, event, active }` | start or stop an event |
| client → server | `EssentialsEvent { service, event, payload }` | an event |

- **The client only runs what it lists.** Each service of `essentials` names its methods and events; the server names one, it never sends
  code, and a name that is not listed (or inherited, such as `constructor`) answers `not-supported`.
- **A call that waits for the user** (a permission prompt, a file dialog) has no timeout (`EssentialsCallOptions.Interactive`); the
  others end after `RouteablesOptions.EssentialsTimeout` (30 s). A command that waits for the device is answered as going on, as for a
  dialog: the connection stays free to carry the answer.
- **Reconnection.** While the connection is gone, calls wait for the client. When it resumes the session and announces itself again, a
  write it may have missed is sent again (`EssentialsCallOptions.Replayable`; the preferences use it), a question fails as `lost`, the
  events are asked again. The writes the client had not acknowledged are applied again over the values it announces.
- **What the client sends is not trusted.** An answer to a call that does not wait in the session is dropped, the names are checked, the
  error codes are reduced to the known ones (`not-supported`, `permission-denied`, `not-enabled`, `timeout`, `invalid-argument`,
  `failed`, `cancelled`, `not-ready`, `lost`, `limit`), at most `EssentialsMaxPendingCalls` (64) calls wait per session. A location or a
  network state is what the device says, never a proof.
- `RouteableEssentialsException.ToMauiException()` gives the MAUI exception of a failure (`FeatureNotSupportedException`,
  `PermissionException`…).

## Limits of a browser

- iOS asks for the motion sensors from a user gesture only: a call of the server is not one. Start them from a command the user triggered
  and, if needed, ask once from a button of the page (`DeviceMotionEvent.requestPermission()`).
- `IScreenshot` is a PNG chosen by the user (`getDisplayMedia`); the server does not convert it to JPEG.
- `IEmail` cannot attach files through `mailto:`; `IAppActions` does not exist in a browser.
- The Dart client does not announce Essentials yet: its view models see `FeatureNotSupportedException` and the default values.
