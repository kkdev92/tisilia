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

**Controllers and minimal APIs.** Controllers serialize with MVC's options (`AddJsonOptions`), minimal APIs with
`ConfigureHttpJsonOptions`. When the two describe types otherwise — another naming policy, other converters — a type both
serve is a model on each side, and its TypeScript name gets a number on one of them. One converter type with other
settings on each side (two `JsonStringEnumConverter`s with different naming policies) is reported (SV16): give both sides
the same converter settings.

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
Server-written local range extremes can be read, but values whose UTC instant is outside years 1–9999 cannot be sent back
through the STJ reader.

**DateTime dictionary keys.** A server's dictionary compares DateTime keys by their ticks, ignoring Kind, and System.Text.Json
converts a key written with an offset to the server's own time. Keys the client writes differently can therefore be one key
on the server, which then keeps the last value without an error. The client refuses keys with the same ticks, and two keys of
one instant written with different offsets. For the other keys with an offset, declare the time zone the server runs in:

```csharp
builder.Services.AddTisilia(o =>
{
    o.ApiId = "orders";
    o.DateTimes.ServerTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
});
```

The contract then carries the zone's UTC offsets for the instants from 1900 to 2199, read from .NET itself, and the client
refuses exactly the keys that the server reads as one: the repeated hour when daylight saving time ends turns
`2026-10-25T02:30:00+02:00` and `2026-10-25T02:30:00+01:00` into one key in Berlin. Without the declaration, and for keys
outside those years, the client sends several keys that include one with an offset only when no time zone can make two of
them one key — such keys more than 28 hours apart, and more than 14 hours from the keys without an offset, since .NET
offsets are at most 14 hours. A single key is always sent. The offsets come from the zone data of the machine that exports
the contract, and Windows and Linux describe the history of some zones differently: export with the zone data production
uses, and export again when it changes. Outside Development the application logs a warning when its own zone has other
offsets than the declared one, and `tisilia doctor` reports both zones.

## 7. What is not supported, and how it is reported

Finite raw/file uploads, scalar/complex form binding, buffered/incremental downloads, typed SSE and the XML bodies of MVC's
XmlSerializer formatters are supported (examples below). A body that another MVC input or output formatter reads or writes
in a format other than JSON (DataContractSerializer, a custom formatter), or an XmlSerializer body whose type has a form the
client does not write or read ([XML bodies](#xml-bodies)), is exported as binary content of that media type with a warning
that names the reason: the client sends the bytes it is given and receives the bytes the server writes, and the contract
does not describe their shape. A minimal API reads and writes models as JSON only, so a model body declared with another
format there is diagnosed, as is a body no formatter reads.

Recursive forms, and form dictionaries of models or with keys other than strings, integers and Guids are diagnosed. A parameter bound by custom code — an MVC `[ModelBinder]`, or a minimal API parameter bound by its type's
`BindAsync` (`IBindableFromHttpContext<T>`) — is exported as declared (below) and diagnosed without a declaration. MVC form models, strings, enums, numbers, dates and times, Guids, booleans and
files are supported. A route, query, header or form value of a type the server reads with its own `TryParse`, `IParsable<T>` or (MVC)
`TypeConverter`, which no codec describes, is exported as text with a warning (SV30): the client sends any string, and the
server answers the texts its parser refuses. MVC's ApiExplorer describes such a controller parameter as a string; export
reads its own type. A handler that returns `Results.Ok(value)` or an `IActionResult` without response metadata is
described from its return paths ([below](#responses-read-from-the-handler)); a path the source does not fix is diagnosed
with its place. `TypedResults.Json(value, options)` is supported with `WithTisiliaJsonOptions<T>`; undeclared dynamic
options remain diagnosed.

`ReferenceHandler.Preserve` works for JSON requests and responses, shared and cyclic values included. In responses,
System.Text.Json writes `$id` first on objects and dictionaries and wraps mutable collections (`List<T>`, `HashSet<T>`, a
collection declared as an interface) in `{"$id": …, "$values": […]}`; arrays, immutable collections, structs and
`JsonObject`/`JsonElement` values carry no metadata. The contract marks each position (`referenceMetadata` on its
server-write wire), and the client reads the metadata only there: a dictionary key, a struct member or a JSON value named
`$id` is data. A value the server writes a second time in the same response — a shared object or a cycle — arrives as
`{"$ref": …}` and decodes as the value already decoded, so `order.lines[0].product === order.lines[1].product`, and a node
whose `parent` is an ancestor is that ancestor. A reference to a value the server first wrote at a position of another type
— a derived type written through its polymorphic base and then directly — fails with a codec failure (`unsupported`) at
that path: the base's position carries the discriminator, so the value has another shape there.

Requests follow what the server reads. Where System.Text.Json reads `$id`/`$ref` and makes the value before reading its
members — a class with a parameterless constructor, a mutable collection, a dictionary — the contract marks the
server-read wire, and the client writes a value it reaches again once with `$id` and then as `{"$ref": …}`, also inside the
value itself; a value reached once carries no metadata. Elsewhere — an array, an immutable collection, a struct, or a type
built through a constructor with parameters, which System.Text.Json makes only after its members and which refuses
metadata while it waits for them — a value reached again is written again, and a value that contains itself there is
refused before sending; without Preserve the server reads trees only, so this holds for every position. Under Preserve
System.Text.Json refuses a property name or dictionary key that starts with `$`, so such a request property is diagnosed
and such a key gets 400. Custom reference handlers are refused under SV20. MVC
options without Preserve next to minimal API options with it (or the other way round) give each side its own models. The
original serializer setting remains recorded in the contract; exporting never changes it.

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
The server must configure its own request-size policy.

From Node (server code, SSR), the body can also be a `ReadableStream<Uint8Array>`: it is sent chunked as it is read
(`duplex: "half"`), without being buffered first. `maxBodyBytes` is counted while it is sent — past it the call ends with a
`limit-failure`, after part of the body has left — and a stream that fails or yields something other than bytes fails the call.
Browsers never get a streamed body from the runtime: Firefox 155 sends a stream as the text `[object ReadableStream]`,
WebKit 26.6 sends an empty body — both answered as successful requests — and Chromium refuses it over HTTP/1.1, so outside
Node the call is refused before anything is sent (`supportsRequestStreams()` tells which applies). A streamed body has no
request identity, so a hydrated Nuxt operation refuses it; a custom `transport.fetch` must accept a stream body with
`duplex: "half"`. Multipart/form media types, multiple media alternatives, MVC raw
body binding and GET/HEAD bodies are diagnosed.

### Responses read from the handler

A minimal API handler that returns `IResult` and an MVC action that returns `IActionResult` or `ActionResult` declare no
response types: the result they return decides at run time what is written. Kkdev92.Tisilia.AspNetCore carries a source
generator that reads, at build time, the handlers of the operations a project registers, and export describes each return
path the way the typed declaration would:

```csharp
app.MapGet("/todos/{id}", async (int id, TodoDb db) =>
        await db.Todos.FindAsync(id) is { } todo ? Results.Ok(todo) : Results.NotFound())
    .WithTisiliaOperation("todos.get");     // 200 with a Todo, and 404 without a body
```

- **Minimal APIs.** A path is the TypedResults type its call creates — `Results.Ok(todo)` creates `Ok<Todo>`, as
  `TypedResults.Ok(todo)` does, and `Results.Ok(null)` creates `Ok` — described by that type's own metadata. The results
  that publish none are described by what they write: `Unauthorized()`, `StatusCode(…)` with a constant, `Problem(…)` and
  `ValidationProblem(…)` (application/problem+json; 500 and 400 unless a constant status code says otherwise),
  `Text(…)`/`Content(…)` (text/plain unless a constant content type says otherwise), `Json(value)` without options (the
  endpoint's JSON options) and `Empty`.
- **Controllers.** A path is what `[ProducesResponseType(typeof(T), status)]` declares, with the static type of the value
  the helper receives: `Ok(todo)`, `NotFound()`, `CreatedAtAction(…, todo)`, `StatusCode(…)`, `Problem(…)`,
  `ValidationProblem(…)`, `Content(…)` and the others of `ControllerBase`. Under `[ApiController]` a bodyless 4xx is a
  ProblemDetails, as for a declared one.
- **Return paths.** Every return statement counts, through `?:`, switch expressions and `async` bodies; a `throw` writes
  no response of its own, and a lambda or local function written inside the handler returns from itself. The order of the
  paths is the order of the responses.

As with a declared type, the body is described by its static type: System.Text.Json writes an instance of a derived class
with its own members, which the client ignores. MVC writes a value passed to `Ok(value)`, `CreatedAtAction(…, value)` or
`StatusCode(status, value)` with the value's own type, whatever the action declares — also `ActionResult<Shape>` and
`[ProducesResponseType<Shape>]` — so a polymorphic type loses its discriminator there: export refuses such a response
wherever it reads the source. Return the value itself from an `ActionResult<T>` action (`return shape;`), which MVC writes
with the declared type. The handler is found by
the operation id written as a constant — `WithTisiliaOperation("id")` on the Map call, or `[TisiliaOperation("id")]` on
the action, method or lambda — and export checks that the endpoint runs the handler the source declares it for.

A path the generator cannot read leaves the operation undescribed, and SV34 names it with its place
(`Endpoints/Todos.cs(14,9)`): a result held in a variable or returned by a method of the application, a value passed as
`object`, an anonymous type or a tuple, a status code or content type that is not a constant, `Json(value, options)`, a
file, a redirect, an authentication result, `Ok(null)` in MVC (written as 204), a `ControllerBase` helper the controller
overrides, and a builder the Map call does not create in the same expression. Declare those responses with
`.Produces<T>()` / `[ProducesResponseType]`, or return a typed result. Declared metadata is always used as it is: the
generator only describes endpoints that declare nothing.

The generator runs in the compiler of each project that references the package itself, reads only the handlers of
registered operations, executes nothing, and adds one internal class, `Tisilia.Generated.TisiliaResponseInference`, with
what it read; the source positions in it are relative to the project. Handlers in a project that references only
Kkdev92.Tisilia.Abstractions are not read, and the diagnostic says so.

### Custom binding declarations

Tisilia cannot see what a type's `BindAsync` or an MVC model binder reads, so it exports such a parameter as you declare it:

```csharp
builder.Services.AddTisilia(o =>
{
    o.CustomBinding
        .BindAsync<PageRequest>(reads => reads.Query<int>("page").Query<int>("size", optional: true))
        .BindAsync<Tenant>(reads => reads.Header<string>("X-Tenant"))
        .BindAsync<CurrentUser>(reads => reads.NotFromRequest())          // claims, features: not part of the contract
        .ModelBinder<CsvBinder>(reads => reads.Query<string>(RequestReads.ModelName));
});
```

Each declared value is a route, query or header parameter of a builtin scalar type (or an array or list of one). The client
writes the scalar's canonical text; the application's code parses it, so its binder carries the server acceptance
`server-parsed`. `RequestReads.ModelName` (namespace `Tisilia.AspNetCore.Bindings`) stands for the name a model binder binds
under (the parameter name, or `[ModelBinder(Name)]`). A declared route value must be a single parameter of the route.

`tisilia doctor` (below) with `--allow-execute-binders` calls every declared binding with a request that records
the query values, headers, cookies, body and form it reads (and, for a model binder, the names it asks its value provider for),
and reports a read outside the declaration as a blocker; the declared route values are supplied, not recorded. This runs the
binding code, which may use the application's services: run it with isolated settings.

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
Use `IFormFileCollection` for multiple files. In a minimal API it receives every file part of the request, whatever its
name — as a parameter and as a model member alike — so it must be the operation's only file field (including files inside
nested models); in a controller action it receives the files of its own name, ignoring case. Files require a non-empty filename without quotes, control characters or path separators. Optional fields use
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

Here `[DataMember(Name = "title_text")]` renames the form member. A form name may contain `.`, `[` or `]`; it is sent as
exactly that key. `[IgnoreDataMember]` excludes ordinary properties;
JSON naming policies and JSON ignore attributes do not change form names. Nested models must have one public constructor.
Writable properties and constructor parameters can nest finite models and collections of models.
Constructor parameters are required by the framework even when a C# default exists. Nested required leaves are required by the
client even when their containing property is nullable. Recursion, two members that produce the same key
(a member named `A.B` next to member `A`'s `B`) and custom parsers are diagnosed.
Generation does not execute constructors or setters.

A `Dictionary<TKey, TValue>`, `IDictionary<TKey, TValue>` or `IReadOnlyDictionary<TKey, TValue>` with a string, integer or
Guid key and a scalar, enum or additional-codec value is a `ReadonlyMap` in the generated body, written as `Labels[key]`
(a root dictionary parameter as `[key]`):

```ts
await client.formsCreate({ body: { Labels: new Map([["color", "blue"], ["size.cm", "42"]]) } });
// Labels[color]=blue&Labels[size.cm]=42
```

ASP.NET Core reads a key up to its first `]` and gathers keys ignoring case, merging the values of keys that differ only
in case, so the client refuses a key with `]` and keys that differ only in case before sending. A dictionary must have at
least one entry; omit an optional one instead. A form with a dictionary cannot have a field name with a `[` that no `]`
follows: ASP.NET Core before 10.0.12 does not answer such a request.

Model collection properties use indexed keys such as `Details.Tags[0]`. This also supports string arrays inside models.
An indexed collection must contain at least one item: an empty array would vanish from the form and change its meaning;
omit an optional property to use the server's omission behavior. Enum fields use their exact integer domain in TypeScript,
write CLR member names (independent of JSON aliases), and preserve 64-bit values. MVC refuses undefined enum values before sending;
defined flags combinations work.

MVC reads form values with the request culture (`FormValueProviderFactory`), so a client cannot know which decimal
separator or calendar the server applies. Tisilia writes MVC numbers and dates in a form that every culture reads as the same
value or refuses: digits with a leading `-`, a fraction as an integer mantissa with a negative exponent (`1.5` → `15E-1`,
`1.50` → `150E-2`), a whole `double` written out in full, ISO date-times, and a `DateOnly` with a time
(`2026-10-08T00:00:00`, which no non-Gregorian calendar reads as one of its own dates). A culture whose signs carry a
direction mark (Arabic, Persian, Hebrew and others; 57 cultures on .NET 10) refuses negative numbers and fractions with 400;
no culture binds another value. Minimal APIs read form values with the invariant culture and get the usual canonical text.
Minimal API form values of the additional codec types (`Int128`, `Uri`, `IPAddress`, …) are written as the same text as
those types' route and query parameters; see [Additional codecs](additional-codecs.md).

An MVC `[FromForm]` model is one object in the generated body, named by the model's prefix — `[FromForm(Name)]` or
`[Bind(Prefix)]` when set, else the parameter name:

```csharp
[HttpPost("/orders")]
[TisiliaOperation("orders.create")]
public ActionResult<string> Create([FromForm] Order order, [FromForm] string note) => order.Name;
```

```ts
await client.ordersCreate({ body: { order: { Name: "日本語", Lines: [{ Id: int64("1") }] }, note: "n" } });
// order.Name=…&order.Lines[0].Id=1&note=n
```

MVC reads a model under its explicit prefix, else under the parameter name when some key starts with it, else at the root;
the client always writes the prefix, so the model never reads a sibling field's keys. Its members follow MVC's model metadata:
a record's constructor parameters, the bindable properties under their `[FromForm(Name)]` or `[ModelBinder(Name)]` names,
without `[BindNever]` members or those outside `[Bind("…")]`. `[BindRequired]` members, and under `[ApiController]`
validation-required ones, are required. Collections of models use indexed names; collections of values repeat the name. A
member with another request binding source, such as `[FromQuery]`, is diagnosed: MVC reads it under the model's prefix
(`order.Page` in the query), which is not the name its API description gives. So is a member with its own `[ModelBinder]`.

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

Single `IFormFile` properties work inside model collections. Use `IFormFileCollection` for all files (at the endpoint root or
on a model, as the only file field); use `IReadOnlyList<IFormFile>` for named file collections on a non-collection model.
An `IFormFileCollection` inside a model collection is diagnosed: each item would receive every file. File-list members inside model collections are also diagnosed: the .NET converter reports an empty list as found,
so the surrounding collection cannot terminate. Other file collection shapes remain diagnosed instead of silently submitting files under names the framework does not bind.

### XML bodies

MVC reads and writes XML with XmlSerializer once the application adds its formatters. An action that accepts or produces
only XML has XML bodies in the contract, described with XmlSerializer's own mapping of the type:

```csharp
builder.Services.AddControllers().AddXmlSerializerFormatters();

[ApiController]
public sealed class OrdersController : ControllerBase
{
    [HttpPost("/orders")]
    [Consumes("application/xml")]
    [Produces("application/xml")]
    [TisiliaOperation("orders.create")]
    public ActionResult<Order> Create(Order order) => order;
}

[XmlRoot("order", Namespace = "urn:shop")]
public sealed class Order
{
    [XmlAttribute("id")] public int Id { get; set; }
    public string? Customer { get; set; }
    [XmlElement(IsNullable = true)] public string? Note { get; set; }
    public Priority Priority { get; set; }
    public DateTimeOffset PlacedAt { get; set; }
    [XmlArrayItem("line")] public List<Line> Lines { get; set; } = [];
}

public enum Priority { Low, [XmlEnum("urgent")] High }

public sealed class Line
{
    [XmlAttribute("sku")] public string? Sku { get; set; }
    [XmlAttribute("qty")] public int Quantity { get; set; }
}
```

```ts
import { parseDateTimeOffset } from "@kkdev92/tisilia-runtime";
import { PriorityXml } from "./api/index.js";

const result = await client.ordersCreate({
  body: { id: 7, Customer: "Ada", Note: null, Priority: PriorityXml.High,
    PlacedAt: parseDateTimeOffset("2026-10-09T10:30:00+09:00"), Lines: [{ sku: "a-1", qty: 2 }] },
});
// sends <order xmlns="urn:shop" id="7"><Customer>Ada</Customer><Note xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
// xsi:nil="true"/><Priority>urgent</Priority><PlacedAt>2026-10-09T10:30:00+09:00</PlacedAt><Lines><line sku="a-1" qty="2"/></Lines></order>
```

A model's members carry their XML names — attributes and elements as XmlSerializer names them (`id`, `sku`), character
content (`[XmlText]`) the C# member's name — and an XML model is a TypeScript type of its own, apart from the class's
JSON model: `OrderXmlRequest` for what the server reads, `OrderXmlResponse` for what it writes, `PriorityXml`.
XmlSerializer maps a class once per namespace, so a class used in two namespaces has a model for each
(`LineXmlResponse2`). The root element, namespaces, the order of attributes and elements, `[XmlArray]`/`[XmlArrayItem]`
wrappers and `[XmlElement]` collections that repeat without one are XmlSerializer's; an action that returns an interface
collection (`IEnumerable<Line>`) is written as `ArrayOfLine`, and a `string` declared as XML as `<string>…</string>`.

XmlSerializer requires no member when it reads: an absent one keeps the value the class's constructor gave it. Every
member of a request model is therefore optional, and null is a value only where XmlSerializer reads one — a nillable
element (`[XmlElement(IsNullable = true)]`, a `Nullable<T>` element), which the client writes as `xsi:nil`; elsewhere
the member is left out. In a response model, a member XmlSerializer always writes is required; one with a `Specified`
property or a `ShouldSerialize` method is optional; a nillable one may be null; a reference the server leaves out when
it is null reads as null when it is absent; and a member with `[DefaultValue]`, which the server leaves out when it has
that value, decodes to the default. An enum is a number in TypeScript, with a constant object that names its members
(`PriorityXml.High`), and travels as the member's XML name (`[XmlEnum]`), a `[Flags]` value as the names separated by
spaces; a value that is not a member is refused before sending, because XmlSerializer can neither write nor read it.
Dates, times, durations (`xs:duration`), Guids, `byte[]` (base64, or `DataType = "hexBinary"`) and the floating-point
specials (`INF`, `NaN`) use XmlSerializer's forms.

The client writes UTF-8 without an XML declaration, as one document no deeper than the formatter's `MaxDepth` (32), and
refuses a deeper one before sending. It writes a carriage return, control characters, and a tab or line feed in an
attribute as character references, which the server reads as those characters, and refuses a string with an unpaired
surrogate. It reads a response as UTF-8 XML 1.0 with namespaces and refuses a document type declaration, processing
instructions and entities other than the predefined ones, none of which XmlSerializer writes. XmlSerializer writes a
carriage return in text as a line break, which XML reads as a line feed: `\r` and `\r\n` in a string the server writes
arrive as `\n` (in an attribute they arrive as written). The XML equivalences are G1, and `tisilia conformance`
does not observe XML bodies — it lists them as `xml-not-observed`. An XML response is server-only for Nuxt hydration.

A type with a form the client does not write or read keeps the body as bytes with a warning (SV29) that names the form:
derived types (`[XmlInclude]`) and abstract types; `object` members, which XmlSerializer writes with `xsi:type`; a choice
of elements (`[XmlElement]` or `[XmlArrayItem]` with several types, `[XmlChoiceIdentifier]`); `[XmlAnyElement]` and
`[XmlAnyAttribute]`; character content next to child elements or of a collection; `IXmlSerializable` and `XmlNode` values;
an attribute that lists values and `xml:` attributes; string data types whose white space XmlSerializer collapses
(`token`, `anyURI` …); a time of day with an offset (`DataType = "time"`), qualified and encoded names; a `[DefaultValue]`
on a member that can be null or has a `Specified`/`ShouldSerialize` condition; and two members with the same XML name. A
body is also kept as bytes when a formatter derived from XmlSerializerInputFormatter/XmlSerializerOutputFormatter, one with
other wrapper providers than `AddXmlSerializerFormatters()` gives it, or a DataContractSerializer formatter reads or writes
it, and when it is a `SerializableError` or `ProblemDetails`, which MVC reads and writes through wrapper types.

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
ASP.NET Core writes `byte[]` event data as its bytes, and `object` data as JSON unless the value is a `byte[]`, so neither has
one encoding the contract could describe: such events are text events with a warning, and the client receives each event's
data as the text it is (CR and CRLF become LF; data that is not UTF-8 fails the subscription). Other types that can hold a
`byte[]`, such as `IEnumerable<byte>`, are diagnosed.
A controller action that returns `TypedResults.ServerSentEvents` is exported the same way. Its JSON data uses the minimal API
`JsonOptions` (`ConfigureHttpJsonOptions`), not the MVC ones, because `ServerSentEventsResult<T>` serializes with them.
An empty JSON data field represents ASP.NET's null event data and is accepted only by a nullable data contract.

Subscriptions await each handler and retain no event history. `timeoutMs` applies to the whole subscription, including handler
time, and `maxBodyBytes` to each connection; JSON depth/token/number limits apply to each event. Events already handled stay
handled on cancellation/failure. A regular `client.eventsRead()` buffers a finite event array within the same limits. Explorer
uses this finite view. SSE cannot be automatically hydrated.

A dropped stream reconnects only when the server says it can resume and the caller asks. The server declares that it continues
after the event whose id a reconnecting client sends in `Last-Event-ID` (HTML Standard) — every event then carries its own id:

```csharp
app.MapGet("/events", (HttpContext context) => TypedResults.ServerSentEvents(ReadEventsAfter(context.Request.Headers["Last-Event-ID"])))
    .WithTisiliaEventResume() // [TisiliaEventResume] on a controller action
    .WithTisiliaOperation("events.read"); // ReadEventsAfter yields SseItem<T> with EventId set
```

```ts
const result = await client.eventsReadSubscribe(onEvent, {}, { reconnect: { maxAttempts: 5, delayMs: 3000 } });
// result.reconnections counts the reconnections
```

The client reconnects after a transport failure while connecting or reading — never at EOF, after an HTTP error answer, a
limit or a handler error — waiting the server's latest `retry:` (else `delayMs`), and sends `Last-Event-ID` with the id of the
last event it delivered. It reconnects only where no event can arrive twice: before the first event, or after an event that
named its own id; otherwise the failure is returned. `maxAttempts` counts attempts in a row without a delivered event.
`Last-Event-ID` is not a CORS-safelisted header: a cross-origin server's CORS policy must allow it. Declaring the resume on an
operation that writes no events is diagnosed (SV29). Whether the handler really continues after the given id is the
application's to keep; the contract records the declaration.

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
executes application startup (and, with `--allow-execute-binders`, the declared binding code), so run it with isolated settings;
the flags are not a sandbox. Strict export writes no partial contract.

The contract, Codec ABI, portable DSL, configuration and conformance formats use version 0.1.
Export and generate with the same Tisilia version. After a format change, re-export the contract and regenerate clients and evidence;
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
| `… declares no response types` (SV34) | the message names the return path the source generator could not read; declare that response with `.Produces<T>()` / `[ProducesResponseType]`, or return a typed result |
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
