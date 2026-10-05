# Getting started

From an ASP.NET Core API to a typed TypeScript client: register the operations, export the contract, generate the client,
call the API. Every command below has been run against fresh `dotnet new webapi`, `dotnet new webapi --use-controllers` and
`dotnet new webapiaot` applications.

Requirements: .NET 10, Node 24, TypeScript 6. Every release so far is a pre-release: NuGet needs `--prerelease`, and the npm
runtime is installed at the version of the CLI that generated the client (`dotnet tisilia version` prints it).

## 1. Register the operations (server)

```text
dotnet add package Kkdev92.Tisilia.AspNetCore --prerelease
```

Minimal APIs:

```csharp
builder.Services.AddTisilia(o => o.ApiId = "weather-api");
var app = builder.Build();

app.MapGet("/weatherforecast", () => forecasts)
   .WithTisiliaOperation("weather.forecast");     // only registered endpoints become operations

if (app.Environment.IsDevelopment())
{
    app.MapTisiliaContract();                     // GET /__tisilia/contract (optional)
}
```

Controllers:

```csharp
[HttpGet]
[TisiliaOperation("weather.forecast")]
public IEnumerable<WeatherForecast> Get() => ...;
```

With `<ImplicitUsings>enable</ImplicitUsings>` (the templates' default) the package imports `Tisilia`,
`Tisilia.AspNetCore`, `Tisilia.AspNetCore.Bindings` and `Tisilia.AspNetCore.Codecs`; set
`<TisiliaImplicitUsings>false</TisiliaImplicitUsings>` to write the usings yourself. `AddTisilia` changes no JSON, CORS or
authentication setting: the contract describes the options the application already has.

What the API says about itself for OpenAPI — XML comments, `WithSummary`/`WithDescription`, `[Description]`, response
descriptions, validation attributes — becomes the contract's documentation: the Explorer shows it and the generated client
carries it as JSDoc ([Documenting the API](api-documentation.md)). The package turns on the XML documentation file the `///` comments travel
in, without the compiler's warnings about missing comments; a project that sets `GenerateDocumentationFile` itself keeps its
own setting.

## 2. Install the CLI

```text
dotnet new tool-manifest            # once per repository
dotnet tool install --local Kkdev92.Tisilia.Tool --prerelease
dotnet tisilia help
```

## 3. Export the contract

```text
dotnet tisilia export --project src/Api --allow-execute-project
```

`export` builds and starts the application (its startup code runs: migrations, hosted services), reads the endpoints and the
effective System.Text.Json options, writes `src/Api/tisilia.contract.json` and stops the application. It never listens on a
port, so a running development server does not get in the way, and it starts without the launch profile: pass what the
application needs at startup in the environment (`--environment` sets `ASPNETCORE_ENVIRONMENT`, Development by default).
The application gets 120 seconds after the build (`--timeout <seconds>`) and is stopped after them; the contract file is
replaced only when the export succeeds. Problems are reported per operation with a code, a JSON pointer and a fix, and kept
in `src/Api/tisilia.contract.json.diagnostics.json`.

## 4. Configure and generate

```text
dotnet tisilia init --contract src/Api/tisilia.contract.json --output src/web/src/api
dotnet tisilia generate --config tisilia.json
```

`init` writes `tisilia.json` with the default settings and takes the `apiId` from the contract. Paths in the
config are relative to it and may not leave its directory, so put it where both the contract and the output are below it
(the repository root). `--module-mode nodenext` targets Node ESM; the default `bundler` targets Vite, Nuxt and other bundlers.

The output directory is owned by the generator (`tisilia.generation-manifest.json`): edited or foreign files are never
overwritten without `--force`. `dotnet tisilia check --config tisilia.json` compares the output with what would be generated
and exits with 4 when it is out of date — the CI step. Generated code compiles with `strict`, `exactOptionalPropertyTypes`,
`noUncheckedIndexedAccess`, `verbatimModuleSyntax`, `noUnusedLocals`, `noUnusedParameters` and `erasableSyntaxOnly`.

**Keeping the contract stable.** The contract's `semanticHash` covers the bytes of the assemblies that implement your own
codecs or behaviors, so build them reproducibly. Set `<IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>`
and `<EnableSourceLink>false</EnableSourceLink>` for local builds: otherwise the .NET SDK writes the git commit into the
assembly's version and into its PDB's Source Link map, which a deterministic build hashes into the assembly, and every commit
changes the hash (`export` warns about both). An assembly
also records the path it was built in, so export where the contract is maintained, or with `ContinuousIntegrationBuild=true`
(which normalizes the paths). It is compiled anew whenever a library it references changes (a package update included), so
export and generate again after such an update: a client from the previous contract answers `contract-mismatch` under the
contract guard. In CI, `generate` and `check` against the committed contract need no export at all.

**Line endings.** Git for Windows checks text files out with CRLF unless `.gitattributes` says otherwise (its installer sets
`core.autocrlf=true`, GitHub's Windows runners included). `generate` and `check` read CRLF as LF in the contract and in the
generated output, so such a checkout is up to date and needs no `--force`. Module scripts are different: the contract records
their bytes, so `codec install-additional` and `codec generate` write a `.gitattributes` (`-text`) beside theirs, and the
scripts of your own modules need the same — `export` warns about a script with CRLF line endings.

## 5. Call the API

```text
npm install @kkdev92/tisilia-runtime@<version>     # the version `dotnet tisilia version` prints
```

```ts
import { formatDateOnly } from "@kkdev92/tisilia-runtime";
import { createWeatherApiClient } from "./api/index.js";

const client = createWeatherApiClient({ baseUrl: "https://localhost:7001" });
const result = await client.weatherForecast();
if (result.kind === "response") {
  for (const forecast of result.data) {
    if (forecast === null) continue;          // see "null" below
    console.log(formatDateOnly(forecast.date), forecast.temperatureC, forecast.summary);
  }
} else {
  console.error(result.kind, result);         // unexpected-response, codec-failure, transport-failure, timeout, …
}
```

The client is named after the `apiId` and has one method per operation. A call never throws for an HTTP or decoding problem:
the result is either a declared response case (`kind: "response"`, with `caseId`, `status` and — unless the case has no
body — `data`) or one of these failures: `unexpected-response` (a status or media type the contract does
not declare), `codec-failure` (a declared case whose body does not decode), `transport-failure` (network, CORS, a redirect —
the client never follows one —, or arguments that cannot be encoded: `reason: "request-encoding"`), `cancelled`, `timeout`,
`limit-failure` and `contract-mismatch`.
When an operation declares several cases, narrow on `caseId` or `status` before reading `data`.

Options: `baseUrl` (required; may carry a path, `https://host/app`), `credentials` (`same-origin` by default),
`credentialProvider` (credential headers such as `Authorization`, asked for on every call), `headers` (non-credential headers
the operation allows), `signal`, `limits`, and the contract guard `expectedSemanticHash` + `semanticHashHeader` (with
`o.EmitSemanticHashHeader = true` on the server: a response produced under another contract is `contract-mismatch`), and
`transport: { fetch }` for another fetch implementation, a test double for one; a body it does not give as a web stream is
read whole and then checked against `maxBodyBytes`. Every method takes per-call overrides as its second argument;
`limits` merge entry by entry with the client's.

**Browsers.** The current versions of Chrome, Edge, Firefox and Safari; older ones are not a target (no polyfills). The
runtime, generated clients and portable codec modules are ES2022. Web Crypto's `subtle` exists in secure contexts only: a
page served over plain HTTP on a LAN address gets the runtime's own SHA-256 instead.

A browser page on another origin than the API needs CORS on the API — Tisilia changes no CORS setting. With the contract
guard, also expose its header (`policy.WithExposedHeaders("x-tisilia-contract")`): a cross-origin response hides every header
CORS does not list, and the guard, finding none, lets every response through (the Nuxt module warns about that in
development).

## 6. What the types say

| C# | TypeScript |
|---|---|
| `int`, `short`, `byte`, `float`, `double` | `number` |
| `long`, `ulong` | `Int64`, `UInt64` (bigint; `int64(…)`) |
| `decimal` | `Decimal` (exact; `decimalFromString`, `formatDecimal`) |
| `Guid` | `Guid` (`guid("…")`) |
| `DateOnly`, `TimeOnly`, `DateTimeOffset`, `TimeSpan` | `DateOnly`, `TimeOnly`, `DateTimeOffset`, `Duration` (`parseDateOnly("2026-10-02")`, `formatDateOnly(…)` and the like; `dateTimeOffsetFromDate(new Date())` / `dateTimeOffsetToDate(…)` convert to and from a JS `Date`, which holds milliseconds) |
| `DateTime` | declared first (below) |
| `string`, `bool`, enums, records, classes, arrays, lists, dictionaries | `string`, `boolean`, `number` + a const object of members, interfaces, readonly arrays, `TisiliaMap` |

The other .NET types Tisilia supports — `Int128`, `BigInteger`, `Uri`, `IPAddress` and more — are in
[Additional codecs](additional-codecs.md).

**null.** System.Text.Json writes `null` for a reference member whenever the value is null, whatever its annotation, so
response members are `T | null` unless the profile enforces annotations:
`builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.RespectNullableAnnotations = true)` (controllers:
`AddJsonOptions`). Elements of collections stay nullable in responses: no option makes System.Text.Json refuse to write a
null element. A handler's own return value follows its annotation: `Item? (…) => …` and methods returning
`Item?` give `Item | null` (minimal APIs write the JSON `null`), controllers write a null value as 204 (a `no-content` case),
and code without annotations (`#nullable disable`) counts as nullable. A lambda whose return type is inferred carries no
annotation at all, so its value counts as not null — declare the return type when it can return null.

**DateTime.** System.Text.Json writes a `DateTime` according to its runtime `Kind` (`Z`, the local offset or nothing), so its
wire is declared using `o.DateTimes.Default` in `AddTisilia`, plus `o.DateTimes.Add(typeof(T), "Member", …)` for exceptions.
Choose Utc only when the existing values are Utc, Unspecified for zone-less values, or Local for server-zone-dependent JSON.
Mixed Kind needs a separate codec; a custom converter needs its own binding. This declaration never changes values or serializer
settings. HTTP route/query/header binders differ from JSON and do not support a Local declaration.

## 7. What is not supported, and how it is reported

Finite file downloads with explicit metadata are supported as bounded buffers. Uploads, streaming APIs, server-sent events,
XML serialization, `multipart/form-data` and form binding remain unsupported. Export also diagnoses a result
whose response the metadata cannot describe: `Results.Ok(value)` / `IActionResult` without `.Produces<T>()` /
`[ProducesResponseType]` (use `TypedResults.Ok(value)` or `Results<Ok<T>, NotFound>`), and `TypedResults.Json(value, options)`,
whose serializer options are chosen at run time.

Routes come from the mapped endpoint's resolved RoutePattern, including MapGroup/MVC prefixes and defaults. Optional
`{id?}`, defaulted `{page=1}` and `{filename}.{ext?}` routes omit only structurally optional values. Explicit defaults are sent;
route defaults and handler defaults are recorded separately. Omission that shifts a later argument or makes a complex value
ambiguous is rejected before sending. Null, empty and undefined remain distinct. `*` accepts a single segment; `**` preserves
leading, repeated and trailing slash boundaries. Literal percent is encoded once; unsafe dot segments, backslashes and unpaired
surrogates are rejected. Incoming constraints remain the server's; outbound transformers need a separate supported binding.

```csharp
app.MapGet("/reports/{id?}", (string? id) => Results.File(bytes, "application/pdf", "report.pdf"))
    .Produces<Microsoft.AspNetCore.Mvc.FileContentResult>(200, "application/pdf")
    .WithTisiliaOperation("reports.get");
```

`Results.File` may keep its `IResult` return type. The standard marker declares a file's status and concrete media; use
`FileStreamResult` for a finite stream. A known concrete FileContentHttpResult/FileStreamHttpResult return plus explicit
status/media also works. Bare File() in .NET 10 does not supply enough ApiExplorer metadata. `byte[]` JSON remains base64 JSON.
The client result holds `data.bytes`, `data.contentType`, and an optional sanitized `data.suggestedFileName`; browser CORS must
expose Content-Disposition for a name to be available. A binary application/json file stays bytes. 206 remains partial.

`maxBodyBytes` counts actual bytes delivered by Fetch after decompression. Timeout covers credential-provider, fetch, read and
decode using one deadline. Custom fetch adapters must provide a Web ReadableStream or a native null body; no arrayBuffer fallback
is used. The result is a complete buffer within the limit, never a partially successful file or a streaming API.

Use `tisilia doctor --project ./MyApi --allow-execute-project --format json --output doctor.json` before export. It reports
each selected operation, grouped causes, effective DateTime declarations, and selected/analyzed/unanalyzed counts. Exit 0 means
metadata is supported, 3 means blockers, and 6 means startup/metadata analysis is incomplete. HTTP remains unobserved. The command
executes application startup, so run it with isolated settings; the flag is not a sandbox. Strict export writes no partial contract.

Version 0.2.0-alpha requires contract 0.4. Re-export and regenerate together, or retain 0.1.0-alpha for existing 0.3 contracts.
Codec ABI, portable DSL, configuration and conformance formats remain 0.3; string replacement is not a migration.

Native AOT and trimmed applications (`dotnet new webapiaot`) are supported: their source-generated `JsonSerializerContext` is
recorded in the contract with what decides its execution path, and `export` runs the application under the JIT
(`dotnet run`), so the published binary is not needed. A type the context does not list is reported by the export, as it
would fail when the endpoint is called — add `[JsonSerializable(typeof(T))]`.

## 8. Troubleshooting

| message | what to do |
|---|---|
| `config apiId 'a' does not match the contract's apiId 'b'` | the config points at another API's contract; fix `contract` or `apiId` |
| `module '…': artifact '…' is not installed at …` | copy the module there; for `tisilia-additional`: `dotnet tisilia codec install-additional --project <app dir>` |
| `artifact '…' digest … does not match the contract; it differs only in line endings` | git converted the script on checkout: put a `.gitattributes` with `* -text` in its folder, delete the files there and restore them with `git checkout -- .` (for `tisilia-additional`, `codec install-additional` does both) |
| `DateTime … has no declared wire` | declare `o.DateTimes.Default` (section 6) |
| `… declares no response types` (SV34) | add `.Produces<T>()` / `[ProducesResponseType]` or return a typed result |
| `export host exited with <code>` | the application failed to start or the export failed; the application's log is above the message |
| `the application did not finish the export within 120 s` | the application does not call `AddTisilia`, or its startup takes longer: `--timeout <seconds>` |
| `1 difference(s) … run generate` (`check`, exit 4) | regenerate and commit the output |
| a browser call returns `transport-failure`, `reason: "network"`, "Failed to fetch (the cause is not exposed to scripts …)", although the API is up | browsers do not tell scripts why a fetch failed; their developer console does. Usually CORS (section 5) or a redirect, which the client never follows: .NET 10 cookie authentication still sends unauthenticated calls of minimal API handlers returning `IResult` (`Results.Ok(…)`), `string` or `void` to its login page (302) — call `.DisableCookieRedirect()` on them or their group to get 401/403. Node's fetch names it: `reason: "redirect"` |
| a controller call returns `unexpected-response` with status 400 | `[ApiController]` model validation answers 400 with a `ValidationProblemDetails` the action does not declare (responses the pipeline writes before the action runs are not part of its cases); declare `[ProducesResponseType<ValidationProblemDetails>(400)]` to get a typed case |

## Trying a build from source

Pack the packages into a local feed and point NuGet and npm at it:

```text
dotnet pack src/backend/Tisilia.AspNetCore -c Release -o ../feed     # likewise Abstractions, Generator, Tool, Explorer
npm pack -w @kkdev92/tisilia-runtime                                 # then: npm install ../tisilia/kkdev92-tisilia-runtime-<version>.tgz
```

with `<add key="tisilia" value="../feed" />` in the application's `nuget.config` and
`dotnet tool install --local Kkdev92.Tisilia.Tool --add-source ../feed --prerelease`. `scripts/install-check.ps1` runs exactly
this against fresh projects. NuGet takes a version it already holds in its global packages folder (`~/.nuget/packages`)
without asking the feed, so packages packed again under the same version are not picked up: give each local pack its own
version (`-p:VersionSuffix=alpha.local2`) or remove that version from the folder.
