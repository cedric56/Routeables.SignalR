using Routeables.SignalR;
using XAnimals.Server;
using XAnimals.ViewModels;

// The content root is the folder of the server, not the current directory: appsettings.json is found wherever the server is started from
// (Visual Studio, dotnet run, its exe, or `dotnet path/to/XAnimals.Server.dll` from another folder as the CI does).
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });

// The address is http://localhost:5090 (appsettings.json): the dev servers of the web samples proxy /routeables there.
// --urls or ASPNETCORE_URLS choose another one.

// The view models of the sample, served to a JavaScript front end. There is no view on the server: a route is registered
// with its view model alone, and the Angular app draws the state that the server sends. Dialogs, notifications and the note
// modal go to the browser too (Routeables.SignalR registers their services).
builder.Services.AddRouteablesSignalR<MainViewModel>(services => services.AddAnimalsForAngular(), options => options.AddAnimalsExposure());

// The dev servers of the web samples (ng serve, vite) run on their own port and proxy /routeables here (proxy.conf.json, vite.config.ts):
// for them no CORS is needed. Flutter web calls the hub from its own address: the pages of this machine may (a sample, not a policy
// for production). A mobile app sends no origin and needs none.
builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy => policy
    .SetIsOriginAllowed(origin => Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.IsLoopback)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

var app = builder.Build();

app.UseCors();
app.MapRouteables();   // the hub, at /routeables

app.Run();
