# Tisilia

<!-- Tisilia artwork: enable the package banner after an anonymous public image URL is verified. -->

.NET and TypeScript, connected down to what the types mean. Tisilia reads an ASP.NET Core API from the running application
— the endpoints it maps and the System.Text.Json options in effect for each — writes that down as a contract, and generates
a TypeScript client that reads and writes exactly the JSON the server does: 64-bit integers, decimals, GUIDs, dates and
durations keep their exact values, and what the contract cannot describe is reported with a diagnostic instead of being
guessed.

> **Pre-release.** Every version so far is one: `dotnet add package` and `dotnet tool install` need `--prerelease`.

## Install

```bash
dotnet add package Kkdev92.Tisilia.AspNetCore --prerelease
dotnet tool install --local Kkdev92.Tisilia.Tool --prerelease     # after `dotnet new tool-manifest`
```

This readme ships in every Tisilia package.

| Package | Purpose |
|---|---|
| `Kkdev92.Tisilia.AspNetCore` | `AddTisilia`, `WithTisiliaOperation` / `[TisiliaOperation]`, the contract endpoint and the export host |
| `Kkdev92.Tisilia.Tool` | The `tisilia` CLI: `export`, `init`, `generate`, `check`, `diff`, `conformance`, `codec`, `explorer build`, `watch` |
| `Kkdev92.Tisilia.Explorer` | `MapTisiliaExplorer()`: the Explorer page on an authorized route |
| `Kkdev92.Tisilia.Generator`, `Kkdev92.Tisilia.Abstractions` | The contract model, validator and generators the others build on |

The generated client runs on the npm package `@kkdev92/tisilia-runtime`, installed at the version of the CLI that
generated it (`dotnet tisilia version` prints it).

## Getting started

```csharp
builder.Services.AddTisilia(o => o.ApiId = "shop");
var app = builder.Build();

app.MapGet("/orders/{id:guid}", (Guid id) => new Order(id, 9007199254740993L, 1234.50m))
   .WithTisiliaOperation("orders.get");          // only registered endpoints become operations

app.Run();

public sealed record Order(Guid Id, long Revision, decimal Total);
```

```bash
dotnet tisilia export --project src/Api --allow-execute-project     # starts the app once, writes src/Api/tisilia.contract.json
dotnet tisilia init --contract src/Api/tisilia.contract.json --output src/web/src/api
dotnet tisilia generate --config tisilia.json
```

The generated `createShopClient(...).ordersGet({ id })` returns either the declared response — `revision` as an exact
`Int64`, `total` as a `Decimal` that keeps `1234.50` — or a named failure such as `unexpected-response`,
`codec-failure`, `transport-failure` or `timeout`; it does not throw for an HTTP or decoding problem.

`Kkdev92.Tisilia.AspNetCore` changes no JSON, CORS or authentication setting. It turns on the XML documentation file of the
project that references it, without warnings for missing comments, so that `///` comments reach the contract, the Explorer
and the client's JSDoc; a project that sets `GenerateDocumentationFile` itself keeps its own setting.

## Running code

`export` and `conformance` start your application and `explorer build` runs a package build, so each refuses to without
`--allow-execute-project`, `--allow-execute-adapters` or `--allow-execute-build`. `generate`, `check`, `validate`, `diff`,
`init` and `watch` never execute anything, and no command fetches code from a contract or a remote origin.

## Documentation

| | |
|---|---|
| [Readme](https://github.com/kkdev92/tisilia/blob/main/README.md) | Features, what is guaranteed, known limitations, how it works |
| [Getting started](https://github.com/kkdev92/tisilia/blob/main/docs/getting-started.md) | From an API to a typed client, step by step, and the messages you may meet |
| [Changelog](https://github.com/kkdev92/tisilia/blob/main/CHANGELOG.md) | What changed in each release |
| [Security](https://github.com/kkdev92/tisilia/blob/main/SECURITY.md) | What is in scope, and how to report a vulnerability |

Source, issues and discussion: <https://github.com/kkdev92/tisilia>

Author and other projects: <https://kkdev92.dev/>

## License

The original code is MIT, which is what the package metadata says. The packaged `NOTICE` names what the packages carry from
others — Vue in the Explorer page, portions of the Explorer's inline icon set adapted from Feather Icons, and the part of
the route template reader adapted from ASP.NET Core. All are MIT-licensed; keep the notice with the package if you
redistribute it.

The mascot icon and Explorer artwork have separate branding and identity usage guidance in the included
`BRAND-ASSET-POLICY.md`. It permits normal factual, editorial, documentation, and integration references, without
granting endorsement or trademark rights. The source code remains MIT-licensed.
