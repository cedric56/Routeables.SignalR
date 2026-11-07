# Versions and releases

## Two packages, one version

The NuGet package `Routeables.SignalR` and the npm package `@routeables/client` are released together and carry the same version. A client and a server of the same version always work together.

The versions follow [Semantic Versioning](https://semver.org): a change that breaks an application (an API, a default, the protocol) is a major version; a feature is a minor version; a fix is a patch.

## The protocol

The messages between the client and the server have their own version, `RouteableProtocol.Version` on the server and `PROTOCOL` in the client (2). The client says it first (`Hello`); a server of another protocol refuses the client with a message that says to update it, rather than letting it misread the messages. The protocol changes only with a major version.

| Protocol | Packages | What it brought |
| --- | --- | --- |
| 1 | before 2.0.0 (never released) | Pages and property changes, no versions. |
| 2 | 2.0.0 | Generations and versions per target, patches, `Completed`, `Hello` and the resumption of a session, error codes. |

## Routeables

Routeables.SignalR is developed against the sources of [Routeables](https://github.com/cedric56/Routeables), at a revision pinned in the CI (`ROUTEABLES_REF`). It is released against the published packages of Routeables (`-p:RouteablesVersion=…`): the NuGet package depends on `Routeables` and `Routeables.Abstraction` of that version.

## Releasing

1. Move `ROUTEABLES_REF` if needed, and set the repository variable `ROUTEABLES_VERSION` to the version of the Routeables packages to depend on.
2. Write the changes in [CHANGELOG.md](CHANGELOG.md).
3. Set `VersionPrefix` in [Directory.Build.props](Directory.Build.props) and `version` in [clients/typescript/package.json](clients/typescript/package.json) to the new version.
4. Tag `vX.Y.Z` and push the tag: [the release workflow](.github/workflows/release.yml) packs against the Routeables packages, installs the NuGet package in an empty application, tests the client, and publishes both packages (secrets `NUGET_API_KEY` and `NPM_TOKEN`).

A preview is a tag with a suffix (`v2.1.0-preview.1`).
