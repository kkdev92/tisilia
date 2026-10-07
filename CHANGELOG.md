# Changelog

All notable changes to Tisilia are documented here. NuGet and npm packages share the same release version;
contract and codec format versions are tracked separately. Release dates are UTC.

From 1.0.0 onward, Tisilia follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
Before 1.0, compatibility may change between releases; breaking changes will be called out explicitly.

## [Unreleased]

### Added

- **Forms** across export, generated clients, the interpreter and Explorer: URL-encoded and multipart contracts for
  minimal API scalar and repeated fields, nested models and scalar constructor parameters (form-specific `DataMember`
  names and ignored members), indexed collections of scalars and models, nested constructor objects and root lists, and
  finite `IFormFile`/`IFormFileCollection` uploads; MVC string and file fields. Minimal API and MVC enums use their exact
  integer domains. Indexed objects reject empty or sparse elements before sending, nested files share the upload budget,
  and the application's CSRF policy remains enforced. Explorer edits form fields and selects files; its form snippets
  construct branded integers and GUIDs so copied code passes strict TypeScript checking.
- **Raw uploads** for minimal API `Stream` / `PipeReader` bodies with explicit `Accepts` metadata. Generated and
  interpreted clients send bounded `Uint8Array` bodies byte for byte. Explorer includes a file selector; Nuxt identities
  distinguish raw body bytes, empty uploads and omitted bodies.
- **Streaming downloads.** The runtime's `download` API streams declared binary responses into an asynchronous sink with
  ordered writes, a shared deadline, cancellation and decompressed-byte limits. Declared JSON errors still use their
  normal codecs.
- **Server-sent events.** Typed SSE response contracts for minimal API endpoints, generated `operationSubscribe` methods and
  the runtime `subscribe` API. Text and lossless JSON data decode incrementally, with ordered asynchronous handlers,
  cancellation and connection byte/time limits. `isFailure` treats a completed download or subscription as success.
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

### Changed

- `ReferenceHandler.Preserve` no longer blocks operations whose JSON bodies use only builtin scalar codecs (including
  nullable scalars and base64 bytes). Structured JSON and custom reference handlers still fail closed under SV20.

### Fixed

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
- The runtime's `RequestBodyDescriptor` is now a union of JSON, binary and form descriptors. Binary and form descriptors
  carry `kind`; a JSON descriptor omits it or uses `"json"`. Narrow the union before reading `codec`.
- Finite SSE calls return event arrays; subscriptions do not retain events and cannot be automatically hydrated.

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

[Unreleased]: https://github.com/kkdev92/tisilia/compare/v0.1.0-alpha...HEAD
[0.1.0-alpha]: https://github.com/kkdev92/tisilia/releases/tag/v0.1.0-alpha
