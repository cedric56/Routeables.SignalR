# XAnimals view models

The view models the samples draw (`XAnimals.Common`, `XAnimals.Birds`, `XAnimals.ViewModels`) belong to the [Routeables](https://github.com/cedric56/Routeables) repository (`samples/Bases`, `samples/Modules`), where the desktop and Blazor samples use them too. They are not packaged.

These three projects compile **their sources**, from the Routeables checkout (`RouteablesRoot`, `../Routeables` by default), **against the Routeables package** that Routeables.SignalR depends on (`RouteablesPackageVersion`). Referencing the projects of Routeables instead would bring a second copy of each Routeables assembly next to the package (CS1704) — and, at another revision, another API.

- The checkout of Routeables must be at the revision of that package (`ROUTEABLES_REF` in the CI): move both together.
- The generator of routes of Routeables runs in `XAnimals.ViewModels` only, as in Routeables (`UseRouteablesGenerator`, see [Directory.Build.targets](../../Directory.Build.targets)).
- Nothing to edit here: a change to these view models is made in Routeables.
