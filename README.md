# Tisilia

![Tisilia — .NET and TypeScript, connected down to what the types mean.](./assets/brand/tisilia/banners/readme-banner-light.jpg)

[![NuGet](https://img.shields.io/nuget/vpre/Kkdev92.Tisilia.AspNetCore)](https://www.nuget.org/packages/Kkdev92.Tisilia.AspNetCore)
[![npm](https://img.shields.io/npm/v/@kkdev92/tisilia-runtime/next)](https://www.npmjs.com/package/@kkdev92/tisilia-runtime)
[![CI](https://github.com/kkdev92/tisilia/actions/workflows/ci.yml/badge.svg)](https://github.com/kkdev92/tisilia/actions)
[![OpenSSF Best Practices](https://www.bestpractices.dev/projects/15238/badge)](https://www.bestpractices.dev/projects/15238)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![.NET](https://img.shields.io/badge/.NET-10.0-blue.svg)](https://dotnet.microsoft.com/)

.NET and TypeScript, connected down to what the types mean. Tisilia reads an ASP.NET Core API from the running
application — the endpoints it maps and the System.Text.Json options in effect for each — writes that down as a contract,
and generates a TypeScript client from it that reads and writes exactly the JSON the server does. A 64-bit integer stays an
integer, a decimal keeps its scale, a date keeps its offset, and what the contract cannot describe is reported, never
guessed.
_Built for teams that need consistent value semantics across .NET and TypeScript._

> **Status:** `0.2.0-alpha`, the second preview. On every CI run the packed packages are installed into a fresh ASP.NET
> Core application and a fresh TypeScript project: the CLI exports and validates the application's contract, generates a
> client, compiles it under strict settings, and calls the running application through it, and an int64 beyond 2^53 and a
> `+09:00` offset have to arrive exactly.
>
> The contract format, Codec ABI, portable codec DSL and conformance protocol all use version 0.1. Format versions
> are separate from the package version and may still change incompatibly before 1.0.0.

---

## Table of Contents

- [Features](#features)
- [Installation](#installation)
- [Quick Start](#quick-start)
- [Why Tisilia](#why-tisilia)
- [What Is Guaranteed](#what-is-guaranteed)
- [Known Limitations](#known-limitations)
- [How It Works](#how-it-works)
- [Platform Requirements](#platform-requirements)
- [Security and Privacy](#security-and-privacy)
- [Documentation](#documentation)
- [Contributing](#contributing)
- [Support & Maintenance Policy](#support--maintenance-policy)
- [License](#license)
- [Brand assets](#brand-assets)

---

## Features

- **A Contract From the Application Itself**: `tisilia export` builds and starts the application, records the endpoints it registered for Tisilia and the serializer options each one uses, and stops it again
- **Exact Values**: `long`, `ulong`, `decimal`, `Guid`, `DateOnly`, `TimeOnly`, `DateTime`, `DateTimeOffset` and `TimeSpan` arrive as exact TypeScript values (bigint, scaled decimal, 100 ns ticks), never through a JavaScript `number` that cannot hold them
- **Types That Say What the Server Does**: nullability as the serializer writes it, enums by name or number, dictionaries with their key codecs, polymorphic types as tagged unions with literal discriminators, and one result type per declared response case
- **Responses Read From the Handlers**: a handler that returns `Results.Ok(value)` or an `IActionResult` declares no response types; a source generator reads its return paths at build time, and export describes them as the typed declaration would
- **Fail Closed**: a converter without a binding, or a response whose wire format cannot be determined exactly — anything the contract cannot describe exactly is a diagnostic with a fix, checked by 55 semantic rules (SV01–SV55)
- **Calls That Return, Not Throw**: a call gives a declared response case or a named failure — `unexpected-response`, `codec-failure`, `transport-failure`, `timeout`, `limit-failure`, `contract-mismatch` — and never follows a redirect
- **File Transfers**: raw uploads to `Stream` / `PipeReader` endpoints — finite bytes everywhere, and streamed from Node — buffered downloads, and incremental downloads to an asynchronous sink, with byte limits and cancellation
- **Forms and Events**: URL-encoded and multipart forms with nested models, constructor parameters, enums, exact scalar values, indexed collections and file parts; typed SSE subscriptions with incremental decoding, backpressure and cancellation, reconnecting with `Last-Event-ID` where the server declares that it resumes
- **Endpoint JSON Options**: `WithTisiliaJsonOptions<T>` exports and enforces the settings used by `TypedResults.Json` and MVC `JsonResult`, with separate request, response and error profiles
- **Custom Converters Included**: a hand-written `JsonConverter` is paired with a TypeScript codec module, or both sides are generated from a portable codec definition; the contract pins each module by its digest
- **Conformance You Can Show**: `tisilia conformance` runs a suite derived from the contract through the server's real converters and the generated client, and records the result as evidence that marks operations qualified
- **Explorer**: a page the application serves that runs every call through the generated client's own codecs — request and response side by side, an Authorize dialog, the API's own documentation, Japanese and English
- **Nuxt**: request-scoped clients, allowlisted header forwarding during SSR, and hydration envelopes that carry exact values from the server render to the browser
- **The API's Own Words**: XML comments, summaries, descriptions, validation attributes and `[Obsolete]` reach the Explorer and the client's JSDoc
- **Zero Third-Party Runtime Dependencies** in the .NET packages and in `@kkdev92/tisilia-runtime`

---

## Installation

```bash
# --prerelease, because every version so far is one and the CLI does not consider
# pre-release versions unless asked.
dotnet add package Kkdev92.Tisilia.AspNetCore --prerelease

dotnet new tool-manifest                     # once per repository
dotnet tool install --local Kkdev92.Tisilia.Tool --prerelease

# The runtime of the generated client, at the version of the CLI that generated it.
npm install @kkdev92/tisilia-runtime@<version>   # dotnet tisilia version prints it
```

| Package | Purpose |
| --- | --- |
| `Kkdev92.Tisilia.AspNetCore` | `AddTisilia`, `WithTisiliaOperation` / `[TisiliaOperation]`, the contract endpoint and the export host |
| `Kkdev92.Tisilia.Tool` | The `tisilia` CLI: `export`, `init`, `generate`, `check`, `diff`, `conformance`, `codec`, `explorer build`, `watch` |
| `Kkdev92.Tisilia.Explorer` | `MapTisiliaExplorer()`: the Explorer page on an authorized route |
| `Kkdev92.Tisilia.Generator`, `Kkdev92.Tisilia.Abstractions` | The contract model, validator and generators the others build on |
| `@kkdev92/tisilia-runtime` | What a generated client runs on: the lossless JSON parser, the codecs, the HTTP pipeline |
| `@kkdev92/tisilia-nuxt` | The Nuxt 4 module |
| `@kkdev92/tisilia-explorer` | The Explorer's source, for `tisilia explorer build` |

---

## Quick Start

On the server, register the operations a client should see. Nothing else changes: Tisilia sets no JSON, CORS or
authentication option of its own.

```csharp
builder.Services.AddTisilia(o => o.ApiId = "shop");
var app = builder.Build();

app.MapGet("/orders/{id:guid}", (Guid id) => new Order(id, 9007199254740993L, 1234.50m))
   .WithTisiliaOperation("orders.get");          // only registered endpoints become operations

app.Run();

public sealed record Order(Guid Id, long Revision, decimal Total);
```

Export the contract, then generate the client from it:

```bash
dotnet tisilia export --project src/Api --allow-execute-project     # runs the app's startup, writes src/Api/tisilia.contract.json
dotnet tisilia init --contract src/Api/tisilia.contract.json --output src/web/src/api
dotnet tisilia generate --config tisilia.json
```

And call it:

```ts
import { guid } from "@kkdev92/tisilia-runtime";
import { createShopClient } from "./api/index.js";

const shop = createShopClient({ baseUrl: "https://localhost:7001" });
const result = await shop.ordersGet({ id: guid("0f8fad5b-d9cb-469f-a165-70867728950e") });

if (result.kind === "response") {
  result.data.revision;   // 9007199254740993n, an Int64 — not 9007199254740992
  result.data.total;      // a Decimal that still reads "1234.50"
} else {
  console.error(result.kind);   // unexpected-response, codec-failure, transport-failure, timeout, …
}
```

`dotnet tisilia check --config tisilia.json` exits with 4 when the committed client no longer matches the contract — the
step for CI. The [getting started guide](docs/getting-started.md) goes through controllers, the client's options, the type
mapping, what is not supported and the messages you may meet.

---

## Why Tisilia

Tisilia derives its contract from the endpoints and serializer options of a running ASP.NET Core application. This reduces
the manual work needed to keep the contract aligned with the application. Export and generate again when those endpoints
or options change. The contract supports the following checks:

- What the serializer options change — naming, number handling, enum names, ignore conditions, required members,
  polymorphism — is recorded per endpoint, and the client applies exactly that
- A value the server writes is decoded into a type that can hold it; nothing is rounded to a JavaScript `number` on the way
- A custom converter is part of the contract, paired with a TypeScript codec that describes its value and wire formats
- What cannot be described exactly is refused at export with a diagnostic that names the fix; there is no `any` to fall
  back to
- The conformance suite compares server and client behaviour for cases derived from the contract and records the results
- Code runs only when you allow it: exporting and conformance start the application, so they need
  `--allow-execute-project` / `--allow-execute-adapters`, and generating never executes anything

Tisilia targets applications on .NET 10 with a TypeScript front end — a browser app, Nuxt or Node — that need these checks
as part of development and CI. It generates clients for ASP.NET Core APIs and requires the application to run during export.

---

## What Is Guaranteed

- **A contract that breaks a rule never reaches a client.** The exporter checks its own output against the schemas and the
  55 semantic rules and writes nothing when one fails, and `validate`, `generate`, `check` and `conformance` check again
  whatever contract they are given. A schema that uses a keyword the evaluator does not implement cannot even load
  (`ValidationTests`, `SchemaEvaluatorTests`)
- **Values keep their value.** The JSON parser keeps every number as written, and int64, decimal, binary floats, GUIDs,
  dates, times and durations are parsed and written the way System.Text.Json does
  (`src/frontend/runtime/test/primitives.test.ts`, `json.test.ts`). CI calls a fresh application through a generated client
  and requires the int64 and the offset to arrive exactly (`scripts/install-check.ps1`)
- **The same contract, the same client.** A contract is canonical JSON whose `semanticHash` is computed over its RFC 8785
  form, generation is deterministic, and `tisilia check` fails when the committed output differs from what the contract
  generates (`FixtureTests`, `HashingTests`, `JcsTests`)
- **Module code is checked, never fetched.** Every codec module's artifacts are pinned by digest in the contract;
  generation, conformance, the Explorer and `explorer build` refuse a file with other bytes (`PipelineTests`)
- **Credentials stay out of storage.** Generated clients ask a credential provider on every call, the Nuxt module keeps
  credentials out of the payload, and the Explorer holds them in memory only; a test scans the Explorer's sources for any
  storage beyond its two display settings (`src/frontend/explorer/test/explorer.test.ts`)
- **What the packages carry from others is named.** `NOTICE` identifies third-party code and vendored assets included in
  the distributed packages and Explorer bundle. Tests check bundled package licenses against the build's own list and
  directly vendored material, such as inline icons, against `src/frontend/explorer/third-party-sources.json`
  (`src/frontend/explorer/test/notice.test.ts`)

---

## Known Limitations

- **Forms.** Minimal API forms support nested models, constructor parameters, indexed collections of models, root lists,
  dictionaries with string, integer or Guid keys, `DateTime` fields, enums, values of the additional codec types and finite
  file parts. MVC forms support models — nested models, records, collections of models and of values, files — and string,
  enum, number, date and time, Guid, boolean and file fields; MVC reads values with the request culture, so numbers and
  dates are written in a form every culture reads as the same value or refuses — a culture whose signs carry a direction
  mark (Arabic, Persian, Hebrew and others) refuses negative numbers and fractions with 400. A minimal API's
  `IFormFileCollection` — a parameter or a model member — receives every file of the request, so it must be the
  operation's only file field; an MVC one receives the files of its name. Recursive models, dictionaries of models or with
  other keys, MVC dictionaries and file lists at the endpoint root other than `IFormFileCollection` are diagnosed.
- **Values the server parses itself.** A route, query, header or form value of a type that the server reads with its
  own `TryParse`, `IParsable<T>` or (MVC) `TypeConverter`, and that no codec describes, is sent as text: the contract
  does not say which texts the server accepts, the client does not check them, and export warns (SV30).
- **Custom binding.** A parameter bound by its type's `BindAsync` (`IBindableFromHttpContext<T>`) or by an MVC
  `[ModelBinder]` reads the request in its own code, so it is exported as `TisiliaOptions.CustomBinding` declares: each
  value it reads becomes a parameter that code parses. An undeclared one is diagnosed. `tisilia doctor
  --allow-execute-binders` calls the declared code with a request that records the query values, headers, cookies, body
  and form it reads, and reports reads outside the declaration; route values are not recorded.
- **Bodies.** A request carries JSON, XML, a form, or a minimal API `Stream` / `PipeReader` body with one declared media
  type; a response carries JSON, XML, text, finite binary content or server-sent events. XML bodies are the ones MVC's
  XmlSerializer formatters read and write, described with XmlSerializer's own mapping. A type with a form the client
  does not write or read — such as `xsi:type` (derived types, `object` members), a choice of elements, `xs:any`, mixed
  content or `IXmlSerializable` ([the full list](docs/getting-started.md#xml-bodies)) — and a body that another
  formatter (DataContractSerializer, a custom one) reads or writes travel as bytes the contract does not describe, with
  a warning that names the reason. A minimal API reads and writes models as JSON only, so declaring another format for
  one there is diagnosed. Uploads are bounded in memory, except that Node sends a `ReadableStream` body as it is read;
  browsers never get one, because they send a stream as text or as nothing, or refuse it.
- **Server-sent events** are exported for minimal API endpoints and controller actions, with text or JSON data; `byte[]`
  and `object` event data reach the client as text, with a warning. One deadline covers a subscription and one byte
  budget each connection. The client reconnects a dropped stream only when the server declares that it resumes after
  `Last-Event-ID` and the caller asks for it, and only where no event can arrive twice.
- **URL normalization** still excludes values that cannot reach ASP.NET Core unchanged. Optional/default, complex optional,
  catch-all and outbound-transformer routes are supported with explicit incoming values. Proxy behavior requires separate verification.
- **Results without response metadata.** A minimal API handler that returns `IResult` (`Results.Ok(value)`) and an MVC
  action that returns `IActionResult` are described from their return paths, which a source generator reads at build
  time: each path as the TypedResults type it creates, or as `[ProducesResponseType]` with the static type of the value.
  A path it cannot read — a result held in a variable or returned by the application's own method, a value passed as
  `object`, a status code that is not a constant, a file, a redirect — is reported with its place in the source, and that
  operation needs `.Produces<T>()` / `[ProducesResponseType]`. As with a declared type, a derived instance is written with
  its own members, which the client ignores. MVC writes a value passed to `Ok(value)` with the value's own type, whatever
  the action declares, so a polymorphic response written that way loses its discriminator: export refuses it wherever it
  reads the source. Only projects that reference Kkdev92.Tisilia.AspNetCore themselves are read. `TypedResults.Json` and
  MVC `JsonResult` work with `WithTisiliaJsonOptions<T>`; undeclared dynamic JSON options remain diagnosed.
- **`ReferenceHandler.Preserve`.** The contract marks where System.Text.Json writes `$id` (objects, mutable collections as
  `{"$id","$values"}`, dictionaries) and where it writes none (arrays, immutable collections, structs, JSON nodes), so data
  that looks like metadata stays data, and a value the server writes twice — a shared object or a cycle, written as
  `$ref` — decodes as one value. A request writes a value it reaches again once with `$id` and then as `$ref` where the
  server reads references, and writes it again where the server reads none: an array, an immutable collection, a struct
  or a type built through a constructor with parameters, which cannot refer to itself, so a value inside itself there is
  refused. A reference to a value first written at a position of another type (a derived type both through its base and
  directly) is refused, because the value has another shape there. System.Text.Json refuses request property names and
  dictionary keys that start with `$` under Preserve, so such a request property is diagnosed and such a key gets 400.
  Custom reference handlers are refused (SV20). Unused JSON settings do not block binary/bodyless operations.
- **Environment-dependent values.** `DateTime` works without declarations in JSON and HTTP parameters, preserving
  100 ns precision and the wire Kind. Local JSON input is converted to the server zone; HTTP offsets bind as UTC.
  That conversion can turn dictionary keys the client writes differently into one key, and System.Text.Json then keeps
  the last value without an error: with `TisiliaOptions.DateTimes.ServerTimeZone` declared, the client refuses exactly
  those keys; without it, several keys that include one with an offset are sent only when no time zone can make two of
  them one. The declared zone's offsets, `TimeZoneInfo` and `CultureInfo` tie a contract to the zone and culture data of
  the machine that exported it.
- **Evidence is about codecs in the recorded runtime matrix.** It does not certify HTTP routing, downloads, CORS, proxies,
  authentication or another host. Binary-only operations with no codec cases are codec-not-applicable and HTTP-unobserved.
  The runner does not observe XML bodies: their codecs get domain validation only, and the suite lists their round trips
  as not applicable (`xml-not-observed`).
- **One target.** .NET 10, TypeScript 6, Node 24, the current browsers; no polyfills for older ones.

Run `tisilia doctor --project ./MyApi --allow-execute-project --format json` for aggregated adoption diagnostics.
The flag permits application startup, including its side effects; it is not a sandbox. No API handler is probed.
Export the contract and generate its client with the same Tisilia version.
Run `scripts/verify-adoption.ps1` to check routes, mixed DateTime, forms/files/CSRF, raw uploads, SSE, endpoint JSON options, downloads and cancellation against Kestrel and Chromium,
Firefox and WebKit. These checks do not establish compatibility with every proxy, hosting configuration or browser version.

---

## How It Works

```text
ASP.NET Core application        endpoints + the System.Text.Json options in effect for each
        v  tisilia export       builds and starts the app, reads what it registered, stops it
tisilia.contract.json           canonical JSON, a semantic hash, checked against SV01–SV55
        v  tisilia generate     offline: never starts the app, never runs a module
TypeScript client               models, codecs, operations, client — owned by a manifest
        v  tisilia conformance  the suite through the server's converters and the client
evidence                        generate --evidence marks the covered operations qualified
```

The contract is a reviewable file: commit it next to the generated client, and the difference between two versions of an
API is a `tisilia diff` away, per direction (what a client may send, what it may receive).

---

## Platform Requirements

|  |  |
| --- | --- |
| .NET | `net10.0`; ASP.NET Core 10, minimal APIs and controllers |
| TypeScript | 6 or later; generated code compiles under `strict`, `exactOptionalPropertyTypes`, `noUncheckedIndexedAccess`, `verbatimModuleSyntax` and `isolatedDeclarations` |
| JavaScript | ES2022 modules; Node 24; the current versions of Chrome, Edge, Firefox and Safari |
| Nuxt | 4.5 or later, for `@kkdev92/tisilia-nuxt` |
| Native AOT | applications that publish with Native AOT are supported; `export` runs them under the JIT |
| Runtime dependencies | none in the .NET packages or in `@kkdev92/tisilia-runtime` |
| SDK (to build Tisilia) | the version in `global.json`; Node 24 |

---

## Security and Privacy

- **Execution Is Opt-In**: `export` and `conformance` start your application and `explorer build` runs a package build, so
  each refuses to without `--allow-execute-project`, `--allow-execute-adapters` or `--allow-execute-build`; `doctor`
  calls declared custom bindings only with `--allow-execute-binders`. `generate`,
  `check`, `validate`, `diff`, `init`, `watch` and `codec generate` never execute anything
- **No Remote Code**: codec modules are code installed next to the application, bound by id and export name and checked
  against the contract's digests; nothing is fetched from a contract or a remote origin
- **A Source Generator That Only Reads**: Kkdev92.Tisilia.AspNetCore brings a source generator into the build of the
  project that references it. It reads the handlers of the registered operations, executes none of the application's
  code, and adds one internal class with what it read: the responses, and source positions relative to the project
- **Explorer Off Outside Development**: the contract and Explorer routes need both `AllowProduction` and an
  `AuthorizationPolicy` outside Development, and the page is served with a content security policy that allows only its
  own scripts and styles
- **Credentials Are the Application's**: clients take them from a provider on every call; the Explorer keeps them in this
  page's memory and names them as placeholders in the code it shows
- **Bounded Reads**: body bytes, nesting depth, tokens, number length and time are limited in the decoder and the transport
  alike, and a response beyond a limit is a `limit-failure`, never a partial value
- **No Telemetry**: nothing in Tisilia reports usage or contacts a server of its own

For vulnerability reporting, see [SECURITY.md](SECURITY.md).

---

## Documentation

|  |  |
| --- | --- |
| [Getting started](docs/getting-started.md) | From an ASP.NET Core API to a typed client: registration, export, generation, calls, types, troubleshooting |
| [Documenting the API](docs/api-documentation.md) | XML comments and attributes in the contract, the Explorer and the client's JSDoc |
| [Nuxt](docs/nuxt.md) | The Nuxt module: composables, hydration envelopes, header forwarding, request identity |
| [Explorer](docs/explorer.md) | The Explorer page: routes, Authorize, redaction, bundling trusted modules |
| [Additional codecs](docs/additional-codecs.md) | `Int128`, `BigInteger`, `Half`, `Uri`, `IPAddress` and the other .NET types beyond the builtin scalars, and declared `DateTime` wires |
| [Portable codecs](docs/portable.md) | Generating the C# converter and the TypeScript codec of a custom type from one definition |
| [Conformance](docs/conformance.md) | The runner protocol, the suite, grades and evidence |
| [Validation rules](docs/validation-rules.md) | SV01–SV55: what each rule checks and where |
| [Changelog](CHANGELOG.md) | What changed in each release |
| [Contributing](CONTRIBUTING.md) | Building, testing, and the rules a change has to follow |
| [Security](SECURITY.md) | What is in scope, and how to report a vulnerability |

---

## Contributing

```bash
npm ci
npm run build                          # the runtime, the Nuxt module and the Explorer page (embedded into Tisilia.Explorer)
dotnet build src/backend/Tisilia.slnx
dotnet test  src/backend/Tisilia.slnx
npm run typecheck && npm test
```

See [CONTRIBUTING.md](CONTRIBUTING.md) for the design constraints, testing requirements and contribution workflow.

Helpful things when reporting bugs:

- `dotnet tisilia version`, `dotnet --version`, `node --version`, and the TypeScript version
- The diagnostic as printed — its `TIS` code, its `[SV…]` rule, the message and the JSON pointer — or the failure `kind`
  and its fields for a call
- The smallest endpoint and type that show it; the contract excerpt helps too

**Never paste credentials, tokens or real data into an issue.** A reproduction with made-up values is always enough, and
an issue is public and stays that way.

---

## Support & Maintenance Policy

This is a personal project maintained in spare time. It is active, but support is best-effort:
I'll do my best to review issues and PRs, and releases may be a bit slow sometimes — thank you for
your patience.

The `0.x` line is pre-release. Breaking changes are expected before `1.0.0` and are listed in the
[CHANGELOG](CHANGELOG.md). From `1.0.0` onward the public API follows
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Really appreciate you using it 💛

---

## License

The original code is [MIT](LICENSE).

[NOTICE](NOTICE), which ships inside every NuGet package, documents the third-party material Tisilia includes: Vue in the
Explorer page, portions of the Explorer's inline icon set adapted from Feather Icons, and the part of the route template
reader adapted from ASP.NET Core. All are MIT-licensed. The Explorer npm package includes its own
[NOTICE](src/frontend/explorer/NOTICE) covering Vue and Feather Icons.

## Brand assets

The Tisilia source code remains licensed under the [MIT License](LICENSE). Tisilia names, logos, Tisilia-chan artwork,
and other project-identifying assets under [`assets/brand/tisilia/`](assets/brand/tisilia/) have separate branding and
identity usage guidance in the [Tisilia Brand Asset Policy](assets/brand/tisilia/BRAND-ASSET-POLICY.md).

The policy allows normal factual, editorial, documentation, and integration references. It does not grant endorsement
or trademark rights, or permission to adopt Tisilia's identity as an unrelated product's branding. Some artwork was
created with generative-AI tools and subsequently curated and prepared for the project; the policy does not claim that
every individual asset is independently copyrightable in every jurisdiction. Third-party notices remain in [NOTICE](NOTICE).
