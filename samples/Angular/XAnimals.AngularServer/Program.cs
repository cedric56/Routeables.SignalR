using Routeables.SignalR;
using XAnimals.AngularServer;
using XAnimals.ViewModels;

var builder = WebApplication.CreateBuilder(args);

// The address is http://localhost:5090 (appsettings.json), whether the server is started by Visual Studio, dotnet run or its exe:
// the Angular and Vue dev servers proxy /routeables there. --urls or ASPNETCORE_URLS choose another one.

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
