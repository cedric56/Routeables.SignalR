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
| Which session | A session is resumed only with its token (256 random bits, sent to its client only) and only by the user it was created for. | 30 s grace. |
| How much | Argument size, values and depth; state size per property; messages waiting for a slow client. | See the options. |
| What an error says | The message of an unexpected exception is sent in Development only (`DetailedErrors`); `RouteableException` is for messages meant for the visitor. | Generic outside Development. |

Authorization is evaluated at every call with `IAuthorizationService` and the policies of the application. A member that asks for authorization is refused when no authorization service is registered.

## Checklist before production

- [ ] The hub requires authorization: `MapRouteables().RequireAuthorization()` after `UseAuthentication()` and `UseAuthorization()`.
- [ ] `Exposure` is `Explicit` (the default), and no `ExposeTypes` predicate is wider than intended.
- [ ] Every settable property is meant to be set by any user who sees the page, or carries `[RouteableAuthorize]`.
- [ ] Every command that changes data carries the authorization it needs (`[Authorize]` on the method or the class), and validates its arguments as an API would.
- [ ] The pages that are not for everybody carry `[Authorize]` on their class. If the application replaces the route guards (`services.Replace(...IRouteGuard...)`), `RouteableAuthorizationGuard` is registered again.
- [ ] Policies that depend on data that changes (a role revoked, an account closed) read it when they are evaluated: they are evaluated at every call, not once per connection.
- [ ] `DetailedErrors` is not forced on outside Development.
- [ ] The limits suit the application (`MaxCollectionItems`, `MaxStateNodes`, `MaxArgumentLength`, `MaxPendingMessages`, SignalR's `MaximumReceiveMessageSize`).
- [ ] Several servers use sticky sessions.

## What is not protected by the library

- The validation of business rules: a command receives what the client sends, within the limits; it decides whether it makes sense.
- Cross-site requests: SignalR uses the authentication of the application. With cookies, configure CORS and the cookie (`SameSite`) as for any endpoint.
- Rate limiting: use ASP.NET Core's rate limiter on the endpoint. The session runs the calls of a client one at a time whatever SignalR's `MaximumParallelInvocationsPerClient` says.
