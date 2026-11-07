using Routeables.SignalR;
using XAnimals.AngularServer;
using XAnimals.ViewModels;

var builder = WebApplication.CreateBuilder(args);

// The view models of the sample, served to a JavaScript front end. There is no view on the server: a route is registered
// with its view model alone, and the Angular app draws the state that the server sends. Dialogs, notifications and the note
// modal go to the browser too (Routeables.SignalR registers their services).
builder.Services.AddRouteablesSignalR<MainViewModel>(services => services.AddAnimalsForAngular(), options => options.AddAnimalsExposure());

// The Angular dev server (ng serve) runs on its own port and proxies /routeables here (see proxy.conf.json), so no CORS is needed.
var app = builder.Build();

app.MapRouteables();   // the hub, at /routeables

app.Run();
