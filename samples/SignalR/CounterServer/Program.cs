using CounterServer;
using Routeables.Abstraction;
using Routeables.SignalR;

var builder = WebApplication.CreateBuilder(args);

// No menu, so no main view model to write. The route is registered by name, one line per page.
builder.Services.AddRouteablesSignalR(services =>
    services.RegisterRoute<CounterView, CounterViewModel>(route: "/counter"));

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapRouteables();   // the hub, at /routeables

app.Run();
