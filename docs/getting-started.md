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

The output directory is owned by the generator (`tisilia.generation-manifest.json` records what it wrote): a file it did not
write is never overwritten, and a generated file you edited only with `--force`. A manifest `generate` cannot read — broken,
or written by a version of Tisilia with another format — stops it, `--force` included: delete the generated files and the
manifest, or generate into an empty directory. `dotnet tisilia check --config tisilia.json` compares the output with what
would be generated and exits with 4 when it is out of date — the CI step. Generated code compiles with `strict`,
`exactOptionalPropertyTypes`, `noUncheckedIndexedAccess`, `verbatimModuleSyntax`, `noUnusedLocals`, `noUnusedParameters`
and `erasableSyntaxOnly`.

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
| `DateTime` | tagged UTC / Local wire / Unspecified, 100 ns ticks; no declaration required |
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

**DateTime.** No declaration is required. The builtin `datetime` scalar generates
`DateTime = DateTimeUtc | DateTimeUnspecified | DateTimeLocalWire`. `parseDateTime(text)` reads `Z`, a numeric offset,
or no suffix into the corresponding tagged record, retaining all seven fractional digits in `bigint` ticks.
It never uses the browser's time zone or a JavaScript `Date`. JSON with a numeric offset becomes Local in the server's zone;
HTTP route/query/header inputs with an offset become UTC under ASP.NET Core's `AdjustToUniversal` binder.
A response is decoded by its own suffix, so both behaviors remain visible. Minimal API DateTime form fields are also supported:
scalar form parameters use `AdjustToUniversal`, while model members use invariant `IParsable<DateTime>` and convert zoned
input (including `Z`) to the server local zone. Form mapping is independent of JSON Kind declarations.

`o.DateTimes.Default` and `o.DateTimes.Add(typeof(T), "Member", …)` optionally narrow the wire to `Utc`, `Unspecified` or
`Local`; `Mixed` restores the union for a member under a narrowed default. A fixed `Local` HTTP declaration remains invalid.
These settings never change application values or serializer settings. Custom converters still need their own binding.
DateTime dictionary equality ignores Kind; the client rejects equal-tick keys and refuses multi-key requests containing Local
because zone conversion and DST can create collisions. A singleton Local key is supported. Server-written local range extremes
can be read, but values whose UTC instant is outside years 1–9999 cannot be sent back through the STJ reader.

## 7. What is not supported, and how it is reported

Finite raw/file uploads, scalar/complex form binding, buffered/incremental downloads and typed SSE are supported (examples below).
Streaming uploads, XML serialization, recursive forms, form dictionaries and custom form binders remain unsupported.
MVC form strings and files are supported; culture-dependent MVC numeric form fields need an explicit binding. Export also diagnoses a result
whose response the metadata cannot describe: `Results.Ok(value)` / `IActionResult` without `.Produces<T>()` /
`[ProducesResponseType]` (use `TypedResults.Ok(value)` or `Results<Ok<T>, NotFound>`). `TypedResults.Json(value, options)`
is supported with `WithTisiliaJsonOptions<T>`; undeclared dynamic options remain diagnosed.

`ReferenceHandler.Preserve` is accepted for JSON bodies consisting only of builtin scalars (including nullable scalars
and base64 bytes), whose converters emit no reference metadata. Structured JSON, arbitrary JSON values, module codecs and
custom reference handlers still require support for their reference semantics and are refused under SV20. The original
serializer setting remains recorded in the contract; exporting never changes it.

Routes come from the mapped endpoint's resolved RoutePattern, including MapGroup/MVC prefixes and defaults. Optional
`{id?}`, defaulted `{page=1}` and `{filename}.{ext?}` routes omit only structurally optional values. Explicit defaults are sent;
route defaults and handler defaults are recorded separately. Omission that shifts a later argument or makes a complex value
ambiguous is rejected before sending. Null, empty and undefined remain distinct. `*` accepts a single segment; `**` preserves
leading, repeated and trailing slash boundaries. Literal percent is encoded once; unsafe dot segments, backslashes and unpaired
surrogates are rejected. Incoming constraints remain the server's. Outbound transformers are supported: client arguments are
the values to bind on the incoming request. They are not passed through `LinkGenerator` or `TransformOutbound`. For example,
`id: "MyArticle"` reaches the handler unchanged even if `LinkGenerator` would produce `myarticle`. Resolved controller/action
token transformations remain part of the exported route literals.

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
is used. Ordinary generated calls return a complete buffer within the limit.

For incremental downloads, use the exported operation descriptor with the runtime's `download` function:

```ts
import { download } from "@kkdev92/tisilia-runtime";
import { reportsGetOperation } from "./api/index.js";

// writer is an application-owned WritableStreamDefaultWriter<Uint8Array>.
const result = await download(reportsGetOperation, {}, { baseUrl: "https://localhost:7001" },
  async (chunk, signal) => {
    signal.throwIfAborted();
    await writer.write(chunk);
  });
if (result.kind === "download") {
  await writer.close();
  console.log(result.file.bytesWritten, result.file.contentType);
} else {
  await writer.abort(); // discard an incomplete destination
  // Declared JSON error cases still return kind:"response", decoded with their codecs.
}
```

The sink receives only declared binary responses, after status, media type and the configured contract guard pass. Writes
are awaited in order; their time is included in the same deadline. Chunks are not retained by the runtime. The decompressed
byte limit still applies, including to 206 responses. A failure can leave partial data in the destination; commit it only
after `kind: "download"`. Sink errors become a fixed `transport-failure` without exposing the thrown message. The caller
owns closing, aborting and cleaning up its sink, and should observe the supplied signal during long writes.

For raw uploads, declare a minimal API's body with exactly one concrete media type (no parameters or wildcards):

```csharp
app.MapPost("/uploads", async (Stream body, CancellationToken cancellationToken) =>
{
    using var received = new MemoryStream();
    await body.CopyToAsync(received, cancellationToken);
    return TypedResults.Ok(received.Length);
})
    .Accepts<Stream>("application/octet-stream")
    .WithTisiliaOperation("uploads.create");
```

```ts
await client.uploadsCreate({ body: new Uint8Array([0, 255, 195, 40]) });
```

`PipeReader` with `Accepts<PipeReader>` works too. This uses ASP.NET Core's raw body binding, with no JSON/base64 encoding
and no filename header. `byte[]` JSON bodies keep their existing base64 semantics. Required bodies accept an empty
`Uint8Array`; only `Accepts<Stream>(isOptional: true, "application/octet-stream")` allows omission. The client validates
`maxBodyBytes` before requesting credentials or sending, and copies the selected byte view before asynchronous work.
The server must configure its own request-size policy. Multipart/form media types, multiple media alternatives, MVC raw
body binding and GET/HEAD bodies are diagnosed.

### Form fields and files

```csharp
app.MapPost("/forms", ([FromForm] string title, [FromForm] long id) => new { title, id })
    .Accepts<IFormCollection>("application/x-www-form-urlencoded")
    .WithTisiliaOperation("forms.create");
app.MapPost("/files", (IFormFile file) => SaveFileAsync(file))
    .WithTisiliaOperation("files.create");
```

```ts
await client.formsCreate({ body: { title: "日本語", id: 9007199254740993n } });
await client.filesCreate({ body: { file: { fileName: "report.bin", bytes: new Uint8Array([0, 255]) } } });
```

Keep the application's antiforgery middleware. Supply its configured token header through `credentialProvider` and the
matching cookie through Fetch credentials (browser) or the server credential provider. Tisilia never disables antiforgery.
Use `IFormFileCollection` for multiple files; it receives all file parts, so it cannot share an operation with another file
parameter. Files require a non-empty filename without quotes, control characters or path separators. Optional fields use
omission, not null; repeated scalar fields use arrays. MVC's blank-to-null string binding rejects blank strings before sending.
The complete encoded request, including multipart headers/boundaries, is bounded by `maxBodyBytes`; file bytes are copied before
credentials are awaited. Forms use deterministic encoding so previews and Nuxt identities describe the actual request.

On .NET 10.0.12, ASP.NET Core's reflection-based minimal API binder cannot build an endpoint with a `[FromForm] string[]`
parameter: it finds a `TryParse` on `string` that does not have the shape it expects. The application starts, but routing
cannot build its endpoints, so requests fail with 500; `doctor` reports incomplete analysis (exit code 6) because the endpoint
metadata cannot be enumerated. Arrays of numbers, such as `int[]` or `long[]`, bind normally.

Nested Minimal API form models use their actual form names as keys in the generated body:

```ts
await client.formsCreate({ body: {
  "title_text": "日本語", "Details.Id": 9007199254740993n,
  "Details.Tags": ["one", "two"], Mode: 9007199254740993n,
} });
```

Here `[DataMember(Name = "title_text")]` renames the form member. `[IgnoreDataMember]` excludes ordinary properties;
JSON naming policies and JSON ignore attributes do not change form names. Nested models must have one public constructor.
Writable properties and constructor parameters can nest finite models and collections of models.
Constructor parameters are required by the framework even when a C# default exists. Nested required leaves are required by the
client even when their containing property is nullable. Recursion, dictionaries, ambiguous member names and
custom parsers are diagnosed. Generation does not execute constructors or setters.

Model collection properties use indexed keys such as `Details.Tags[0]`. This also supports string arrays inside models.
An indexed collection must contain at least one item: an empty array would vanish from the form and change its meaning;
omit an optional property to use the server's omission behavior. Enum fields use their exact integer domain in TypeScript,
write CLR member names (independent of JSON aliases), and preserve 64-bit values. MVC refuses undefined enum values before sending;
defined flags combinations work. MVC numeric/date fields remain diagnosed because their binder depends on request culture.

Complex collections group each item's fields under an array. Constructor-bound complex arguments group their own fields:

```csharp
app.MapPost("/orders", ([FromForm] Order value) => value)
    .WithTisiliaOperation("orders.create");
public sealed record Order(List<Line> Lines);
public sealed record Line(long Id, Detail Details);
public sealed record Detail(string Label, string[] Tags);
```

```ts
import { int64 } from "@kkdev92/tisilia-runtime";
await client.ordersCreate({ body: { Lines: [
  { Id: int64("9007199254740993"), Details: { Label: "日本語", Tags: ["one", "two"] } },
] } });
// Lines[0].Id, Lines[0].Details.Label, Lines[0].Details.Tags[0], ...
```

Arrays, `List<T>` and the standard enumerable/list/collection interfaces work for model members; nested collections of
models work too. A root `List<T>` parameter is supplied under its argument name (`body: { values: [...] }`) but writes
`[0]`, `[1]`, or `[0].Id` without that name on the wire. Complex array parameters follow the same root mapping.
Each object must emit at least one field. Empty objects, empty indexed collections and sparse arrays are rejected before
credentials or network access because ASP.NET Core stops at the first missing collection index.
The request uses one byte budget across all nested fields and files. Explorer can add/remove nested rows, choose files and
copy a client snippet with typed scalar constructors. Keep the application's antiforgery and form-size policies configured.

Single `IFormFile` properties work inside model collections. Use `IFormFileCollection` for all files at the endpoint root;
use `IReadOnlyList<IFormFile>` for named file collections on a non-collection model. File-list members inside model collections are also diagnosed: the .NET converter reports an empty list as found,
so the surrounding collection cannot terminate. Other file collection shapes remain diagnosed instead of silently submitting files under names the framework does not bind.

### Server-sent events

```csharp
app.MapGet("/events", () => TypedResults.ServerSentEvents(ReadEvents()))
    .WithTisiliaOperation("events.read"); // ReadEvents returns IAsyncEnumerable<T> or IAsyncEnumerable<SseItem<T>>
```

```ts
const abort = new AbortController();
const result = await client.eventsReadSubscribe(async (event, signal) => {
  signal.throwIfAborted();
  await consume(event.data, event.event, event.id);
}, {}, { signal: abort.signal, limits: { timeoutMs: 300_000, maxBodyBytes: 67_108_864 } });
// Call abort.abort() to stop. kind:"subscription" means the stream ended successfully.
```

String events carry plain text; concrete JSON types use the same lossless response codecs as ordinary JSON. The parser handles
split UTF-8, a leading BOM, CR/LF/CRLF, comments, multiline data, event names, persistent IDs and retry fields. Retry milliseconds
remain decimal text to preserve large values. It rejects invalid UTF-8 and discards an unfinished event at EOF.
`byte[]` and `object` event data require an explicit adapter because ASP.NET can write raw bytes instead of JSON.
Server-sent events are exported for minimal API endpoints; a controller action that returns them is diagnosed (SV29).
An empty JSON data field represents ASP.NET's null event data and is accepted only by a nullable data contract.

Subscriptions await each handler and retain no event history. Limits apply to the entire connection, including handler time;
JSON depth/token/number limits apply to each event. Events already handled stay handled on cancellation/failure. There is no
automatic reconnect or replay; the application owns that policy. A regular `client.eventsRead()` buffers a finite event array
within the same limits. Explorer uses this finite view. SSE cannot be automatically hydrated.

### Endpoint-specific JSON settings

```csharp
var responseOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
    { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
app.MapPost("/custom", (MyDto value) => TypedResults.Json(value, responseOptions))
    .WithTisiliaJsonOptions<MyDto>(responseOptions)
    .WithTisiliaOperation("custom.create");
```

The declaration freezes that options instance, adds response metadata and installs a result filter. The filter checks the
actual `JsonHttpResult<T>`, options instance, non-null value, status and content type before execution. A mismatch is rejected.
The handler must return `JsonHttpResult<T>` (optionally wrapped in Task/ValueTask); union results, JsonTypeInfo overloads and
JsonResult without a declaration still needs an explicit adapter. Use the declaration's `statusCode`/`contentType` arguments when
they differ from 200/application/json, and pass the same values to `TypedResults.Json`.
Incoming JSON continues using the application's existing request options. Response descriptors carry their own profile,
including when one CLR type has different representations in different endpoints.

For MVC, return `JsonResult` (or Task/ValueTask wrapped), pass the same options instance to its constructor, and declare the operation:

```csharp
app.MapControllers().WithTisiliaJsonOptions<MyDto>("custom.create", responseOptions);
// In an action annotated [TisiliaOperation("custom.create")]:
// return new JsonResult(value, responseOptions);
```

The MVC declaration supplies the response metadata and requires the standard System.Text.Json result executor. Its result filter
checks the exact runtime value type, frozen options instance, effective status and UTF-8 media type before execution. Null and
derived runtime values need another explicitly declared representation. Automatic validation/authentication results keep their
existing behavior. Both declaration APIs apply custom settings only to the declared status; other cases use the application profile.

These rules follow the
[ASP.NET Core body-binding documentation](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis/parameter-binding?view=aspnetcore-10.0#bind-the-request-body-as-a-stream-or-pipereader),
the ASP.NET Core 10 source, and the [Streams standard](https://streams.spec.whatwg.org/).
`tests/file-transfer-sources.txt` lists the sources. Reproduce the HTTP status and content-hash record with
`scripts/verify-adoption-sources.ps1 -Sources tests/file-transfer-sources.txt -Output artifacts/file-transfer-sources`.
`tests/forms-events-sources.txt` records the form, SSE and endpoint JSON sources; use the same script with that source list.
`tests/advanced-binding-sources.txt` records the nested-form, route-transformer and MVC JSON sources,
`tests/complex-form-sources.txt` the collection, constructor and file-mapping sources, and
`tests/datetime-sources.txt` the DateTime sources.

Use `tisilia doctor --project ./MyApi --allow-execute-project --format json --output doctor.json` before export. It reports
each selected operation, grouped causes, effective DateTime declarations, and selected/analyzed/unanalyzed counts. Exit 0 means
metadata is supported, 3 means blockers, and 6 means startup/metadata analysis is incomplete. HTTP remains unobserved. The command
executes application startup, so run it with isolated settings; the flag is not a sandbox. Strict export writes no partial contract.

Version 0.1.0-alpha uses version 0.1 for the contract, Codec ABI, portable DSL, configuration and conformance formats.
Export and generate with matching packages. After a format change, re-export the contract and regenerate clients and evidence;
replacing version strings does not migrate existing artifacts.

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
| `DateTime parameter … declared Local` | use the default mixed wire, Utc, Unspecified, or DateTimeOffset (section 6) |
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
