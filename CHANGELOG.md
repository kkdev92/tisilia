# Changelog

All notable changes to this project are documented in this file.

From 1.0.0 onward this project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
Pre-1.0 releases follow it in spirit; their breaking changes are marked **Breaking**.

The NuGet and npm packages are released together, at the same version. That version and the version of the contract
format — with the Codec ABI and the portable codec DSL — are independent axes: each release says which format version it
reads and writes, and a new format version is a breaking change for anyone with a committed contract.

Dates are UTC, taken from when the packages were published.

## [Unreleased]

Unreleased 0.2.0-alpha. Reads and writes contract 0.4; Codec ABI, portable codec DSL and conformance suite/protocol remain 0.3.

### Breaking

- Contract 0.4 requires a resolved route plan and adds bounded binary response cases. Re-export and regenerate with matching
  0.2.0-alpha packages; retain 0.1.0-alpha tooling for 0.3 contracts. Existing evidence needs a new matching contract/closure run.
- Custom fetch adapters must expose a Web ReadableStream or native null body. The unbounded arrayBuffer fallback is removed.

### Adoption compatibility

- Optional/default routes, complex optional separators, `*` single-segment and `**` slash-preserving values use resolved endpoint metadata.
- Finite files use explicit standard Produces markers, bounded Uint8Array results and advisory filenames. Explorer saves existing
  buffers on explicit confirmation; Nuxt keeps SSR file results out of hydration payloads.
- `doctor` aggregates metadata diagnostics without probing handlers, reports incomplete analysis separately, and preserves startup
  execution permission. DateTime guidance describes existing semantics without changing JSON settings.
- Provider/fetch/read/decode share a deadline; codec evidence remains distinct from unobserved HTTP behavior.

### Security

- Transport failures use fixed diagnostic messages so errors from fetch adapters or response streams cannot disclose
  credentials, request data or internal addresses through their message, cause or stack.

### Added

- `Kkdev92.Tisilia.AspNetCore`: `AddTisilia`, and operations registered explicitly with `WithTisiliaOperation` (minimal
  APIs) or `[TisiliaOperation]` (controllers). The export host that `tisilia export` starts records the endpoints and the
  System.Text.Json options in effect for each — naming, number handling, enum forms, ignore conditions, required members,
  polymorphism, source-generated contexts — and refuses what it cannot describe exactly with a diagnostic that names the
  fix. `MapTisiliaContract` serves the contract, and an opt-in response header names the contract a response was produced
  under.
- The API's own documentation in the contract: XML comments, endpoint summaries and descriptions, `[Description]`, response
  descriptions, validation attributes, `[DefaultValue]` and `[Obsolete]`. The package turns on the XML documentation file of
  the project that references it.
- Additional codecs for `Int128`, `UInt128`, `BigInteger`, `Half`, `Uri`, `Version`, `IPAddress`, `Rune`, `IPNetwork`,
  `Index`, `Range`, `Complex`, `JsonValue`, `TimeZoneInfo` and `CultureInfo`; declared `DateTime` wires; enum route, query
  and header parameters.
- `Kkdev92.Tisilia.Tool`, the `tisilia` CLI: `export`, `init`, `validate`, `hash`, `closure`, `generate`, `check`, `diff`,
  `conformance`, `codec generate`, `codec install-additional`, `explorer build` and `watch`, with exit codes a script can act
  on. The commands that run code — `export`, `conformance`, `explorer build` — refuse to without an explicit flag.
- `Kkdev92.Tisilia.Generator`: validation against the JSON Schemas and the 54 semantic rules SV01–SV54, canonical hashing,
  the TypeScript generator (models, codecs, operations, client, JSDoc, an ownership manifest) and the portable codec
  generator.
- The conformance suite: cases derived from the contract, run through the server's converters and the generated client,
  and recorded as `tisilia.conformance-evidence` that `generate --evidence` turns into qualified coverage.
- `@kkdev92/tisilia-runtime`: a lossless JSON parser and writer; exact codecs for int64, decimal, binary floats, GUIDs,
  dates, times and durations; the Codec ABI; and an HTTP pipeline whose calls return a declared response case or a named
  failure.
- `@kkdev92/tisilia-nuxt`: request-scoped clients, allowlisted header forwarding during SSR, and hydration envelopes bound to
  the contract, the request and the page.
- `Kkdev92.Tisilia.Explorer` and `@kkdev92/tisilia-explorer`: an API explorer page served by the application that runs every
  call through the generated client's own codecs, with an Authorize dialog, the API's documentation, and Japanese and
  English.

### Notes

- Targets .NET 10 (`net10.0`), TypeScript 6, Node 24 and the current versions of the major browsers.
- The .NET packages and `@kkdev92/tisilia-runtime` take no third-party runtime dependency.

[Unreleased]: https://github.com/kkdev92/tisilia/commits/main
