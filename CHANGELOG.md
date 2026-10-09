# Changelog

All notable changes to Tisilia are documented here. NuGet and npm packages share the same release version;
contract and codec format versions are tracked separately. Release dates are UTC.

From 1.0.0 onward, Tisilia follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
Before 1.0, compatibility may change between releases; breaking changes will be called out explicitly.

## [Unreleased]

## [0.2.0-alpha] - 2026-10-09

Many endpoints that 0.1.0-alpha refused to export, or described wrongly, are now described as the server reads and
writes them: forms and uploads, server-sent events, XML bodies, handlers that return `IResult` or `IActionResult`, custom
binders and `ReferenceHandler.Preserve` among them. The format versions are unchanged, but contracts exported by this
version can use variants that 0.1.0-alpha tools reject, and a few public .NET members take new optional parameters (see
Compatibility).

### Added

- **Forms** across export, generated clients, the interpreter and Explorer: URL-encoded and multipart contracts for
  minimal API scalar and repeated fields, nested models and scalar constructor parameters (form-specific `DataMember`
  names, which may contain `.`, `[` or `]`, and ignored members), indexed collections of scalars and models, nested
  constructor objects and root lists, dictionaries with string, integer or Guid keys, values of the additional codec
  types, and finite `IFormFile`/`IFormFileCollection` uploads; MVC models (nested models, records, collections of models
  and of values, files) written under the model's prefix, and MVC string, number, date and time, Guid, boolean and file
  fields, with numbers and dates written so that every request culture reads the same value or refuses it. Minimal API
  and MVC enums use their exact integer domains. Indexed objects reject empty or sparse elements before sending, nested
  files share the upload budget, and the application's CSRF policy remains enforced. Explorer edits form fields,
  dictionary rows and selects files; its form snippets construct branded integers and GUIDs, and cast values of the
  additional codec types to their brands, so copied code passes strict TypeScript checking.
- **Raw uploads** for minimal API `Stream` / `PipeReader` bodies with explicit `Accepts` metadata. Generated and
  interpreted clients send bounded `Uint8Array` bodies byte for byte. Explorer includes a file selector; Nuxt identities
  distinguish raw body bytes, empty uploads and omitted bodies. From Node, a `ReadableStream<Uint8Array>` body is sent
  chunked as it is read, with `maxBodyBytes` counted while it is sent; browsers, which send a stream as text or as an
  empty body or refuse it, never get one (`supportsRequestStreams()`), and hydrated Nuxt operations refuse it.
- **Streaming downloads.** The runtime's `download` API streams declared binary responses into an asynchronous sink with
  ordered writes, a shared deadline, cancellation and decompressed-byte limits. Declared JSON errors still use their
  normal codecs.
- **Server-sent events.** Typed SSE response contracts for minimal API endpoints and controller actions, generated
  `operationSubscribe` methods and the runtime `subscribe` API. Text and lossless JSON data decode incrementally, with
  ordered asynchronous handlers, cancellation and connection byte/time limits; `byte[]` and `object` event data arrive as
  text, with a warning. `isFailure` treats a completed download or subscription as success.
- **Resumable server-sent events.** `WithTisiliaEventResume()` / `[TisiliaEventResume]` declares that an endpoint's events
  resume after `Last-Event-ID`; with `reconnect: { maxAttempts }`, the runtime's `subscribe` then reconnects a dropped stream
  after the server's `retry:`, sending the id of the last event it delivered, and only where no event can arrive twice.
- **Endpoint JSON options.** `WithTisiliaJsonOptions<T>` declares and enforces a `TypedResults.Json` response's frozen
  options, type, status and media. `MapControllers().WithTisiliaJsonOptions<T>(operationId, options)` declares an MVC
  `JsonResult`, with a result filter enforcing the frozen settings, exact runtime value type, status and UTF-8 media type.
  The settings apply only to the declared status; automatic and separately declared errors keep the application's
  profile. Request and response profiles remain separate; the same CLR type can have distinct endpoint JSON
  representations.
- **Mixed-Kind `DateTime` by default** in JSON, route/query/header parameters, minimal API scalar/model forms, generated
  clients, the interpreter, portable codecs and Explorer. Tagged UTC/local-wire/unspecified values retain 100 ns ticks;
  optional declarations narrow the wire. DateTime dictionary keys use CLR tick equality; uncertain multi-key Local
  collisions are refused.
- Explicit incoming values on routes with outbound transformers; link generation retains its separate transformer behavior.
- **Custom binding declarations.** `TisiliaOptions.CustomBinding` declares what a type's `BindAsync` or an MVC model binder
  reads — route, query and header values, or nothing from the request — and export describes the parameter that way.
  `tisilia doctor --allow-execute-binders` calls the declared code with a request that records what it reads and reports
  reads outside the declaration.
- **Responses read from the handlers.** A minimal API handler that returns `IResult` (`Results.Ok(value)`,
  `Results.NotFound()`) and an MVC action that returns `IActionResult` declare no response types, and export refused them.
  Kkdev92.Tisilia.AspNetCore now carries a source generator that reads the return paths of registered handlers at build
  time, and export describes each path as the typed declaration would: the TypedResults type it creates, or
  `[ProducesResponseType]` with the static type of the value. A path it cannot read is reported with its place in the
  source (SV34), and declared metadata is always used as it is.
- **DateTime dictionary keys and the server's time zone.** System.Text.Json converts a key written with an offset to the
  server's own time, so keys the client writes differently can be one key on the server, which keeps the last value.
  `TisiliaOptions.DateTimes.ServerTimeZone` declares the zone the server runs in: the contract carries its UTC offsets
  from 1900 to 2199 as .NET computes them, and the client refuses exactly the keys the server reads as one. Without the
  declaration, several keys that include one with an offset are sent when no time zone can make two of them one, instead
  of being refused. Outside Development, the application logs a warning when its own zone has other offsets than the
  declared one, and `tisilia doctor` reports both zones.
- **XML bodies.** An MVC action that accepts or produces only XML through MVC's XmlSerializer formatters
  (`AddXmlSerializerFormatters()`) has typed request and response bodies, described with XmlSerializer's own mapping of
  the type: the root element and namespaces, attributes and elements in XmlSerializer's order, wrapped and repeated
  collections, `xsi:nil`, `Specified`/`ShouldSerialize` members and `[DefaultValue]`, character content, enum and flags
  names, and XmlSerializer's forms of dates, times, durations, Guids and binary data. Generated clients, the interpreter
  and Explorer write and read the documents; XML models are TypeScript types of their own (`OrderXmlRequest`,
  `OrderXmlResponse`). A type with a form the client does not write or read — derived types and `object` members
  (`xsi:type`), a choice of elements, `xs:any`, mixed content, `IXmlSerializable` — keeps the body as bytes with a warning
  that names the form (SV29). XML equivalences are G1, and `tisilia conformance` lists them as not observed.

### Changed

- `ReferenceHandler.Preserve` no longer blocks JSON requests or responses, shared and cyclic values included. The contract
  marks where System.Text.Json writes `$id` and `{"$id","$values"}` in responses, and where it reads them in requests. A
  value the server writes twice (`$ref`: a shared object or a cycle) decodes as one value. A request writes a value it
  reaches again once with `$id` and then as `$ref` where the server reads references, and writes it again where it reads
  none (arrays, immutable collections, structs, types built through a constructor with parameters). A reference to a value
  first written at a position of another type is refused. Custom reference handlers still fail closed under SV20; a request
  property whose JSON name starts with `$`, which System.Text.Json refuses under Preserve, is reported.
- `tisilia conformance` runs key round trips only for codecs that a JSON body uses as a dictionary key, under the profile
  of that body and never under `ReferenceHandler.Preserve`. A scalar that is never a key gets no key cases.
- A route, query, header or form value of a type the server reads with its own `TryParse`, `IParsable<T>` or (MVC)
  `TypeConverter`, which no codec describes, is exported as text with a warning (SV30) instead of failing export: the
  client sends any string, and the server answers the texts its parser refuses.
- A response that an MVC output formatter writes in a declared format other than JSON and that the contract does not
  describe as XML (a DataContractSerializer or custom formatter, or a type with a form XML bodies do not cover), and a
  minimal API string declared with a media type other than JSON, are exported as binary responses with a warning (SV29)
  instead of failing export.
- `@kkdev92/tisilia-explorer` depends on `vite` 8.3.2 instead of 8.3.1.

### Fixed

- An MVC action that declares a polymorphic response type — `ActionResult<Shape>`, `[ProducesResponseType<Shape>]` — and
  passes the value to `Ok(value)` was described as a tagged union, but MVC writes such a value with its own type, without
  the discriminator, so the client failed to decode it. Export now refuses that response wherever it reads the source, and
  says to return the value itself, which MVC writes with the declared type.
- With `TisiliaOptions.DateTimes.Default = Local`, the client sent DateTime dictionary keys of one instant written with
  different offsets as different keys, and the server kept only one of them. These keys now follow the rules of the
  mixed DateTime keys.
- A request value that contains itself made the client recurse until the stack overflowed. It is now refused with a codec
  failure, or written with `$ref` where the server reads references.
- Minimal API parameters bound by their type's `BindAsync` (`IBindableFromHttpContext<T>`) were left out of the contract,
  so a generated client sent nothing for them. Export now describes them as declared, and reports an undeclared one (SV30).
- A body parameter that the endpoint does not read as JSON — `[Consumes("application/xml")]`, or a minimal API that
  declares only `text/json` — was exported as a JSON body, which the server answered with 415. A body MVC's
  XmlSerializer formatter reads is now an XML body; one another MVC input formatter reads is sent as the bytes the caller
  gives, with a warning; a body no client can send is reported (SV29).
- An MVC action that returns a `string` with `[Produces("text/xml")]` was described as a text response, but MVC's string
  formatter writes only `text/plain`: the XML formatter writes the string as `<string>…</string>`, which the client
  returned as the value, markup included. It is now an XML response whose value is the string.
- Controller action parameters whose type MVC reads from one value through a `TypeConverter` or `TryParse` — `Int128`,
  `Version`, `IPAddress`, a type with its own `TryParse` — were exported as plain strings, because MVC's ApiExplorer
  describes them as `string`. Export now reads the parameter's own type: the additional codec types bind through their
  codec binders in the route, query, headers and JSON bodies, and other parsed types are sent as text with a warning.
- A minimal API `IFormFileCollection` parameter, which receives every file of the request, was exported next to file
  fields inside constructor-bound models or model collections, whose files it then received too. The rule now counts
  every file field of the form. A controller action's `IFormFileCollection`, which receives only the files of its name,
  no longer counts as taking every file, and a minimal API model member of that type is supported when it is the
  operation's only file field.
- A type that both controller actions and minimal API endpoints serve was described once, with the JSON options of
  whichever endpoint export read first, also when MVC's options name or convert types otherwise
  (`AddJsonOptions`): a generated client then sent the other side names it does not read — an MVC action with snake_case
  names bound an empty model — and expected names it does not write. MVC models now have their own ids when MVC's
  options describe types otherwise than the minimal API's; one converter type with other settings on each side is
  reported (SV16).
- `tisilia conformance` failed with `adapter.unknown-for-profile` when a codec was reached only from route, query or header
  parameters, form fields or text bodies. Such codecs now get domain-validation cases only.
- A contract whose types are all builtin scalars generated a `models/index.ts` that TypeScript rejected as not a module
  (TS2306), so the generated client did not compile.
- DateTime dictionary keys now honor application converter bindings. Conformance resolves builtin map comparers instead of
  silently skipping their map/object cases, and normalizes Local map keys in the recorded server zone.
- Exported CSRF requirements now follow the endpoint's effective antiforgery metadata, including explicit overrides.
- Explorer's generated client snippets for byte arrays now pass the diagnostic path to `decodeBase64`, so they compile
  against the runtime's public signature.
- `generate` reported a generation manifest it could not read — broken, or written by a version of Tisilia with another
  format — against the config file, at JSON Pointers the config does not have. It now names the manifest, the version that
  wrote it and how to recover; `--force` does not apply. The getting-started guide no longer says that `--force` overwrites
  files the generator did not write.

### Compatibility

- Form requests add `kind: "form"`, whose fields may declare indexed collection encoding and MVC's defined-enum
  restriction; raw uploads add `requestBody.kind: "binary"`; SSE responses and adapters add `kind: "sse"`; a `DateTime`
  without a declaration uses the new builtin `datetime` scalar; request identities add `bodyKind: "binary"` with
  canonical base64 body text. Use matching updated exporter, generator, runtime and Nuxt packages; older tools reject
  these variants.
- The runtime's `RequestBodyDescriptor` is now a union of JSON, binary, form and XML descriptors. Binary, form and XML
  descriptors carry `kind`; a JSON descriptor omits it or uses `"json"`. Narrow the union before reading `codec`.
- Finite SSE calls return event arrays; subscriptions do not retain events and cannot be automatically hydrated.
- Operations with an undeclared `BindAsync` parameter or a body that the endpoint does not read as JSON, which earlier
  versions exported with a wrong request, now fail export with a diagnostic.
- A parameter that the server parses with its type's own parser carries the new server acceptance
  `tisilia.accept.server-parsed@0.1`, and such a form field `serverParsed`; older tools reject both.
- When MVC's JSON options describe types otherwise than the minimal API's (naming or key policy, number handling, ignore
  conditions, reference handling, converter types, resolvers), MVC models get ids of their own: re-export and regenerate.
  A type both sides serve then has two TypeScript names, one of them with a number.
- SSE response bodies may carry `resume: "last-event-id"`, as may the runtime's SSE response descriptors; a subscription
  result reports `reconnections`, and `ClientOptions` takes `reconnect`. Older tools reject the field.
- A generated client's raw upload body is typed `Uint8Array | ReadableStream<Uint8Array>`; `PreparedRequest` may carry a
  `bodyStream` instead of `bodyBytes`, and a custom `transport.fetch` may receive a stream body with `duplex: "half"`.
- Object, array and tagged-union wires may carry `referenceMetadata: true`, and the runtime's object, array, map and
  tagged-union codec descriptors take a matching `referenceMetadata` option; older tools reject the field.
- Form value fields of the additional codec types carry the parameter grammar their text follows (`grammarId`); MVC form
  values that the server reads with the request culture are marked `requestCulture`; form dictionaries are fields of
  `kind: "map"` with a `keyUse`.
- XML bodies add `requestBody.kind: "xml"` and `body.kind: "xml"` with a root element, the wire shapes `xml-text`,
  `xml-element` and `xml-items`, codecs bound to `tisilia.binding.xml-serializer@0.1`, the result adapter kind `xml`, and
  the runtime's `xml` request and response descriptors; request identities of XML bodies use `bodyKind: "binary"`.
  Validation adds SV55. Older tools reject these variants.
- `ContractBuilder.ArrayOf`, `MapOf` and `ObjectOf`, the `ClrTypeMapper` constructor and
  `TisiliaContractExporter.Diagnose` take new optional parameters. Calls compile unchanged, but an assembly compiled
  against 0.1.0-alpha that calls them has to be rebuilt.
- Use matching **0.2.0-alpha** NuGet and npm packages when exporting contracts and generating clients.

## [0.1.0-alpha] - 2026-10-05

The first preview.

### Added

- **ASP.NET Core integration** for explicitly registered minimal API and controller operations. Export captures endpoint
  metadata and System.Text.Json settings, with actionable diagnostics for wire formats that cannot be described exactly.
  Applications can serve the contract and opt into a response header identifying it.
- **API documentation in contracts and generated clients**, including XML comments, endpoint and response descriptions,
  validation attributes, defaults and deprecation notices.
- **Contract validation and TypeScript generation** for models, codecs, operations and clients, with canonical hashing,
  generated-file ownership tracking and portable codec generation.
- **CLI tooling** for project initialization, export, validation, compatibility checks, contract diffs, client generation,
  codec installation, conformance runs, Explorer builds and watch mode. `doctor` reports metadata diagnostics and
  incomplete analysis without invoking endpoint handlers.
- **Exact data codecs and lossless JSON** for int64, decimal, binary floats, GUIDs, dates, times and durations, plus
  optional codecs for additional .NET types and declared `DateTime` wire formats.
- **Typed HTTP calls** that return a declared response case or a named failure. Optional/default routes, complex optional
  separators and catch-all parameters use resolved endpoint metadata; enum route, query and header parameters are supported.
- **Bounded binary responses** declared through standard `Produces` metadata, returned as `Uint8Array` with advisory filenames.
- **Codec conformance evidence** from running contract-derived cases through server converters and generated clients.
  Generation can use this evidence to report qualified codec coverage; it does not establish unobserved HTTP behavior.
- **Nuxt integration** with request-scoped clients, allowlisted SSR header forwarding and hydration envelopes bound to the
  contract, request and page. Binary SSR results are excluded from hydration payloads.
- **API Explorer** using the generated client's codecs, with authorization controls, API documentation, Japanese and
  English interfaces, and file saving from buffered responses after explicit confirmation.

### Security

- Explicit execution permission for commands that run application or build code.
- A shared deadline across provider, fetch, response reading and decoding. Transport failures use fixed diagnostics
  instead of exposing underlying adapter or stream error messages, causes or stacks.

### Compatibility

- Targets .NET 10 (`net10.0`), TypeScript 6, Node.js 24 and current major browsers.
- The contract, Codec ABI, portable codec DSL, control documents and conformance protocol use **0.1**; the standard
  conformance suite uses **0.1.0**.
  Use matching **0.1.0-alpha** NuGet and npm packages when exporting contracts and generating clients.
- Custom fetch adapters must expose a Web `ReadableStream` or a native `null` body.
- The .NET packages and `@kkdev92/tisilia-runtime` have no third-party runtime dependencies.

[Unreleased]: https://github.com/kkdev92/tisilia/compare/v0.2.0-alpha...HEAD
[0.2.0-alpha]: https://github.com/kkdev92/tisilia/releases/tag/v0.2.0-alpha
[0.1.0-alpha]: https://github.com/kkdev92/tisilia/releases/tag/v0.1.0-alpha
