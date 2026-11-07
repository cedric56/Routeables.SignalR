using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Routeables.Abstraction;
using Routeables.SignalR;
using Routeables.SignalR.E2E;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.UseUrls("http://127.0.0.1:0");

// The user is named by the query string (?user=ann&roles=manager): a test scheme, never for a real application.
builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, QueryUser>("test", null);
builder.Services.AddAuthorization();

builder.Services.AddRouteablesSignalR(services =>
{
    services.RegisterRoute<Page, StreamViewModel>(route: "/stream");
    services.RegisterRoute<Page, GuardedViewModel>(route: "/guarded");
    services.RegisterRoute<Page, AdminViewModel>(route: "/admin");
}, options =>
{
    options.CommandGrace = TimeSpan.FromMilliseconds(300);
    options.MaxArgumentLength = 1024;
    options.ReconnectGracePeriod = TimeSpan.FromSeconds(10);
});

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapRouteables().RequireAuthorization();

await app.StartAsync();
// The tests read the address on the first line.
Console.WriteLine("listening " + app.Urls.First());
await app.WaitForShutdownAsync();

internal sealed class QueryUser(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Query["user"].ToString();
        if (string.IsNullOrEmpty(user))
            return Task.FromResult(AuthenticateResult.NoResult());

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user), new(ClaimTypes.Name, user) };
        claims.AddRange(Request.Query["roles"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(r => new Claim(ClaimTypes.Role, r)));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")), "test")));
    }
}
