# Contributing

## Prerequisites

- The .NET SDK named in [`global.json`](global.json) (patch updates of that feature band are accepted)
- Node.js 24 — the root `package.json` declares `engines`, and the npm workspaces use the lockfile
- PowerShell 7 for the scripts under `scripts/`

```bash
npm ci
npm exec -- playwright install --with-deps chromium firefox webkit
npm run build                            # the runtime, the Nuxt module and the Explorer page
dotnet build src/backend/Tisilia.slnx    # embeds the Explorer page into Tisilia.Explorer
dotnet test  src/backend/Tisilia.slnx
npm run typecheck
npm test
```

`scripts/verify.ps1` runs the steps after `npm ci` in that order. The order matters: `Tisilia.Explorer` embeds whatever
`src/frontend/explorer/dist` holds when it is built, and the Explorer's tests read the list of bundled packages that the
page's build writes.

Verification also runs `scripts/verify-adoption.ps1`: a loopback Kestrel fixture, generated/interpreted clients and three real
browsers. `scripts/install-check.ps1` tests packed P0 clients, Explorer source build and a fresh Nuxt SSR/browser consumer.
Tests use ports 4178–4181; no user's API is probed. The matrix runs on Windows and Linux in CI. A local result only certifies
its recorded host/browser versions; an unexecuted CI matrix must be reported as unexecuted.

The builds treat warnings as errors, and `dotnet format src/backend/Tisilia.slnx --verify-no-changes` has to pass. A pull
request that introduces a warning does not pass.

## Line endings matter here

Every text file is LF, in the repository and in every checkout ([`.gitattributes`](.gitattributes)). Codec module
artifacts are hashed byte for byte into contracts, and generated output is compared byte for byte, so a checkout must not
depend on `core.autocrlf`. Do not remove those rules.

## Design constraints

These constraints preserve value semantics, reproducible generation and the project's security boundaries. Please keep
them in mind when proposing or implementing a change.

1. The .NET packages and `@kkdev92/tisilia-runtime` take **no third-party runtime dependency**. The Nuxt module and the
   Explorer depend on what they extend; nothing else does. `Tisilia.AspNetCore.SourceGenerator`, which
   Kkdev92.Tisilia.AspNetCore carries in `analyzers/dotnet/cs`, compiles against the compiler's own API
   (Microsoft.CodeAnalysis.CSharp, at the oldest version a .NET 10 SDK runs in), which the compiler provides when it loads
   it: the package takes no dependency on it.
2. **Fail closed.** What a contract cannot describe exactly is a diagnostic with a fix — never `any`, never a guess, never a
   silent fallback to the base type.
3. **Generating never executes anything.** Only `export`, `doctor`, `conformance` and `explorer build` run code, each behind its
   `--allow-execute-*` flag.
4. **Code is never fetched.** Codec modules are installed next to the application and pinned by digest in the contract.
5. **No value is rounded on the way.** A number a JavaScript `number` cannot hold never passes through one, in the runtime,
   in generated code or in the Explorer.
6. **Credentials stay out** of diagnostics, contracts, generated files, hydration payloads, evidence and browser storage.
7. **Generated output is deterministic.** The same contract and config give the same bytes: no timestamp, machine path or
   locale-dependent formatting goes into a contract or a generated file.
8. **Generated files are owned.** Generation writes only the files its manifest owns and never overwrites an edited or a
   foreign file without `--force`.

## What a change has to bring with it

**Include tests for changes in behaviour and a regression test for each bug fix.**

To confirm that a regression test covers the bug, verify that it fails without the fix and passes with it. Before opening
the pull request:

1. Write the test.
2. Run it without the fix and confirm that it fails for the reported bug. If it passes, adjust the test to reproduce the bug.
3. Apply the fix and confirm that the test passes.

Please describe this verification in the pull request. For changes where an automated test is not applicable, such as
some infrastructure or documentation changes, explain why and describe how you checked the change.

For behaviour that follows .NET or System.Text.Json, check against .NET itself: the programs under `tests/oracle` show
what the runtime does. A fixture that pins a .NET behaviour should say which program reproduces it. For behaviour that
depends on untrusted input, include generated inputs alongside targeted examples (`SchemaEvaluatorTests` mutates
contracts; the runtime tests run corpora).

## Tests

| Suite | Purpose |
|---|---|
| `tests/Tisilia.Contract.Tests` | The contract model, the schemas and the 55 semantic rules, hashing and canonical JSON, the exporter's readers, the TypeScript and portable generators, the CLI as a process, the conformance protocol and evidence, the package's build files, and the release version |
| `src/frontend/runtime/test` | The lossless JSON parser and writer, the primitives against what .NET writes, the codecs, the HTTP pipeline and transport, the contract interpreter, the conformance runner |
| `src/frontend/nuxt/test` | Header partitioning, request identity, cache keys and hydration checks; `config-types.ts` is checked by `npm run typecheck` |
| `src/frontend/explorer/test` | Forms, examples, code snippets, redaction, storage, the page's text in both languages, documentation rendering, and the bundled packages named in `NOTICE` |
| `scripts/install-check.ps1` | The packed packages in fresh projects: a new ASP.NET Core application exported, validated and called through a client generated from it |
| `tests/Tisilia.CultureOracle` | A host with ICU culture data, which `MvcFormCultureTests` starts: the test process has the invariant culture only, and MVC reads form values with the request culture |
| `tests/Tisilia.InferenceFixture` | Endpoints compiled with the source generator as their analyzer, as the package applies it, which `ResponseInferenceTests` export and call; `ResponseInferenceGeneratorTests` drive the generator itself |
| `tests/oracle` | Programs that show what .NET does; not part of any build |

`tests/fixtures` holds contracts, codec modules and a portable project that the tests read. `sample-api.contract.json` is
written by `FixtureTests` from `SampleContracts`; do not edit it by hand.

## Packing

**Never publish a package produced by a local `dotnet pack`.** Release builds set `ContinuousIntegrationBuild`, which
normalizes the paths recorded in the assemblies and keeps the commit in the version and the Source Link map; a local build
leaves both out on purpose (see `Directory.Build.props`). `scripts/install-check.ps1` packs locally to test the packages, not
to ship them.

## Releasing

The NuGet and npm packages are released together, at one version.

1. In a pull request: `VersionPrefix` (and `VersionSuffix`) in `Directory.Build.props`; the version of the root
   `package.json`, of each package under `src/frontend` and of their `@kkdev92/tisilia-runtime` dependency, and
   `runtimeVersion` in `src/frontend/runtime/src/index.ts`; the changelog (move `[Unreleased]` under the new version,
   dated); the README's status line; and `PackageValidationBaselineVersion` in `src/backend/Directory.Build.targets` — the
   release before this one, and none for the first. `ReleaseVersionTests` fails until they agree. Merge it.
2. Dispatch `release.yml` by hand. A manual run is always a dry run: it builds, tests, packs and runs the clean install
   check, and stops before every publishing job. Tags here are immutable, so a pipeline that fails after the tag exists
   costs a version number.
3. Tag `vX.Y.Z` on the merge commit and push the tag.
4. Approve the `release` environment.
