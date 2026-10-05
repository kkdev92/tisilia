# Changelog

All notable changes to Tisilia are documented here. NuGet and npm packages share the same release version;
contract and codec format versions are tracked separately. Release dates are UTC.

From 1.0.0 onward, Tisilia follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
Before 1.0, compatibility may change between releases; breaking changes will be called out explicitly.

## [Unreleased]

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
