# Security

Routeables.SignalR makes server-side objects reachable from a browser. This page says what is protected by the library, what is left to the application, and what to check before production.

## Reporting a vulnerability

Please do not open a public issue. Use the private reporting of GitHub (Security → Report a vulnerability) on this repository.

## The model

The browser is not trusted. Everything it sends is a request; nothing it hides or disables in its own interface protects anything.

| Boundary | Protected by | Default |
| --- | --- | --- |
| Who may connect | The endpoint: `app.MapRouteables().RequireAuthorization()`; `RequireAuthenticatedUser` closes an anonymous connection that reaches the hub anyway. | Open: protect it. |
| What is read | Exposure: only `[RouteableState]` properties (or types accepted by `ExposeTypes`). `[RouteableAuthorize]` on a property hides it from the users who do not meet it. | Nothing that is not exposed. |
| What is written | `[RouteableState(Settable = true)]` and a public setter; `[RouteableAuthorize]` on the property. | Nothing. |
| What is called | `[RouteableCommand]` methods and the `ICommand`s of exposed properties; `[Authorize]` on the method, `[RouteableAuthorize]` on the property that holds the command. | Only what is marked. |
| Which pages | `[Authorize]` on the view model class: the route guard cancels the route change, and a view model the user may not see is never sent, whatever showed it. | Every registered route. |
| Which session | A session is resumed only with its token (256 random bits, sent to its client only) and only by the user it was created for: the same scheme, issuer and identifier (`NameIdentifier`, else `sub`). A session of a user authenticated without an identifier is never resumed. | 30 s grace, for a client that said `Hello`. |
| How much | Argument size, values and depth; state size per property; messages waiting for a slow client; sessions waiting for their client, overall and per user (`MaxDetachedSessions`, `MaxDetachedSessionsPerUser`). | See the options. |
| What an error says | The message of an unexpected exception — thrown by a command, a route change or a getter of the state — is sent in Development only (`DetailedErrors`) and logged; `RouteableException` is for messages meant for the visitor. | Generic outside Development. |

### When authorization is evaluated

- **What the user does** — a command, a property set, a route change — is authorized **at every call**: a policy that reads current permissions sees them change during a session.
- **What the user sees** — a view model, a protected property, at any depth — is authorized **when the page is shown** (a route change, a reconnection, a resynchronisation), for each object the first time it is sent. A permission withdrawn while a page is open is applied to what is read at the next of these, or at once with `RouteableSessions.ReauthorizeAsync(user => ...)` (or `session.ReauthorizeAsync()`): call it where the permission changes.
- **Who the user is** is the user of the connection, as everywhere in SignalR: a cookie or a token that expires is seen when the client reconnects. Set `HttpConnectionDispatcherOptions.CloseOnAuthenticationExpiration = true` (in `MapRouteables(..., options => ...)`) to close the connection when it expires.
- `[Authorize(AuthenticationSchemes = ...)]` does not authenticate again: the user of the connection is checked against the roles and policies. Choose the scheme on the endpoint (`RequireAuthorization` with a policy that names it).

A member that asks for authorization is refused when no authorization service is registered.

## Checklist before production

- [ ] The hub requires authorization: `MapRouteables().RequireAuthorization()` after `UseAuthentication()` and `UseAuthorization()`.
- [ ] `Exposure` is `Explicit` (the default), and no `ExposeTypes` predicate is wider than intended.
- [ ] Every settable property is meant to be set by any user who sees the page, or carries `[RouteableAuthorize]`.
- [ ] Every command that changes data carries the authorization it needs (`[Authorize]` on the method or the class), and validates its arguments as an API would.
- [ ] The pages that are not for everybody carry `[Authorize]` on their class. If the application replaces the route guards (`services.Replace(...IRouteGuard...)`), `RouteableAuthorizationGuard` is registered again.
- [ ] Policies that depend on data that changes (a role revoked, an account closed) read it when they are evaluated; where a permission changes, the application calls `RouteableSessions.ReauthorizeAsync` so that what is shown follows (see above).
- [ ] The connection closes when the authentication expires (`CloseOnAuthenticationExpiration`).
- [ ] `DetailedErrors` is not forced on outside Development.
- [ ] The limits suit the application (`MaxCollectionItems`, `MaxStateNodes`, `MaxArgumentLength`, `MaxPendingMessages`, `MaxDetachedSessions`, `MaxDetachedSessionsPerUser`, SignalR's `MaximumReceiveMessageSize`).
- [ ] The endpoint has a rate limiter (see below), and the copy of the sample server's CORS policy, made for the pages of the developer's machine, is not in production.
- [ ] Several servers use sticky sessions.

## What is not protected by the library

- The validation of business rules: a command receives what the client sends, within the limits; it decides whether it makes sense.
- Cross-site requests: SignalR uses the authentication of the application. With cookies, configure CORS and the cookie (`SameSite`) as for any endpoint.
- Rate limiting: use ASP.NET Core's rate limiter on the endpoint. The session runs the calls of a client one at a time whatever SignalR's `MaximumParallelInvocationsPerClient` says. The limiter applies to the connections (the negotiation and the transport), not to each call: a client that floods calls is slowed by its own session, and a command that is expensive limits itself (a per-user counter in the command, or a policy).

```csharp
builder.Services.AddRateLimiter(o => o.AddPolicy("routeables", http =>
    RateLimitPartition.GetFixedWindowLimiter(http.User.Identity?.Name ?? http.Connection.RemoteIpAddress?.ToString() ?? "",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1) })));
builder.Services.AddSignalR(o => o.MaximumReceiveMessageSize = 64 * 1024);   // the limits of Routeables apply within it

app.UseRateLimiter();
app.MapRouteables(options: o => o.CloseOnAuthenticationExpiration = true)
   .RequireAuthorization()
   .RequireRateLimiting("routeables");
```
