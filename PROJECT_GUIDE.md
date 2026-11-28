# Tisilia project guide

Start here before taking action in this project. Read the section for your task: [reporting a problem](#reporting-a-problem), [verifying a change](#verifying-a-change), [development instructions](#development-instructions), or [security and private reports](#security-and-private-reports). Consult this guide again if your task changes.

## Reporting a problem

Give the output of `dotnet tisilia version`, the .NET, Node.js and TypeScript versions, and the affected stage (export, generate, check, runtime or Nuxt integration). When available, include the diagnostic code, rule and JSON pointer. Use a minimal example with made-up data.

For a type or serializer option that is currently rejected, give a minimal endpoint shape and describe the expected contract semantics.

Include the OS and the smallest affected endpoint and type. Identify conformance, the generated client or Explorer when applicable. For a failed call, include the failure’s `kind` and relevant fields. For a diagnostic, include the `TIS` code, `[SV…]` rule, message, JSON pointer and a fictional contract excerpt. Remove credentials, tokens and real request or response bodies.

## Working on the project

Read the contract and line-ending rules in the development instructions below. Values that the contract cannot describe exactly must produce a diagnostic. Keep third-party runtime dependencies out of the backend packages and the TypeScript runtime. Setup and verification details appear below.

## Verifying a change

Use the rows that apply to your change when preparing a merge candidate.

| Change | What to check |
| --- | --- |
| Backend or frontend behavior | Run `dotnet build src/backend/Tisilia.slnx`, `dotnet test src/backend/Tisilia.slnx`, `npm run build`, `npm run typecheck`, and `npm test` as applicable. Check formatting with `dotnet format src/backend/Tisilia.slnx --verify-no-changes`. |
| Contract or semantic rules | Show the input, expected contract or diagnostic, and coverage on each affected side. A fix should fail its regression test before the change. |
| Diagnostics, generated output or packing | Check that credentials cannot enter diagnostics, generated files, contracts, payloads or browser storage. Follow the packing checks for changes to NuGet or npm contents. |

See the [shared contribution guidelines](https://github.com/kkdev92/.github/blob/main/CONTRIBUTING.md) for the two pull request options.

## Development instructions

The following development and verification requirements apply when preparing product changes for merge. Reference implementations only need a short description of the idea.

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
   (Microsoft.CodeAnalysis.CSharp, at the oldest version a .NET 11 SDK runs in), which the compiler provides when it loads
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

To confirm that a regression test covers the bug, verify that it fails without the fix and passes with it. Before requesting a merge:

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

## Security and private reports

Follow this project’s private reporting instructions below. If its reporting form is unavailable and no other project contact is listed, use the [shared private contact](https://github.com/kkdev92/.github/blob/main/SECURITY.md#private-contact).

## Supported versions

Only the most recent release is supported. The `0.x` line is pre-release: breaking changes are expected before `1.0.0`, and
a fix ships in a new release rather than as a patch to an earlier one. The NuGet and npm packages carry the same version and
move together.

## Reporting a vulnerability

Please report security issues privately through
[GitHub Security Advisories](https://github.com/kkdev92/tisilia/security/advisories/new) rather than opening a public
issue.

Include `dotnet tisilia version`, the .NET, Node and TypeScript versions, and the smallest contract, payload or endpoint that
shows the problem. Do **not** include credentials, tokens, real request or response bodies, or real data: a reproduction with
made-up values is always sufficient, and neither the Explorer nor the runners need credentials to reproduce anything.

## Scope

In scope:

- The runtime reading untrusted JSON: the parser and the codecs accepting what the contract refuses, a value changed or
  rounded on the way, a limit (body bytes, depth, tokens, number length, time) that does not hold
- A generated client or the runtime sending a request to an origin other than the configured one, following a redirect,
  or putting a credential where it does not belong
- The commands' execution boundaries: `export`, `conformance` or `explorer build` running code without their
  `--allow-execute-*` flag, or any other command running code at all
- A codec module accepted with bytes other than the digest the contract records, or fetched from anywhere
- Generation writing outside its output directory, or over a file it does not own
- The Nuxt module's hydration envelopes: a payload carrying a server-only body, a credential, or an envelope accepted for
  another contract, request or scope
- The Explorer: a credential reaching browser storage, a URL or the page's code samples; content rendered as HTML; routes
  served outside Development without `AllowProduction` and an authorization policy

Out of scope:

- The security of your own API — authentication, authorization, TLS — which Tisilia describes but does not change
- Codec modules you write or choose to trust: Tisilia checks that they are the files the contract names, not what they do
- Vulnerabilities in .NET, ASP.NET Core, Node.js, TypeScript, Nuxt or Vue themselves, which belong with those projects

## Security posture

**Code runs only when asked.** `export` and `conformance` start the application and `explorer build` runs a package build,
and each refuses to without its flag. `generate`, `check`, `validate`, `diff`, `init`, `watch` and `codec generate` read and
write files and execute nothing.

**Modules are pinned, never fetched.** A codec module is code installed next to the application, bound by id and export
name. The contract records the digest of each of its artifacts, and generation, conformance, the Explorer's module route
and `explorer build` refuse a file whose bytes differ.

**Responses are untrusted input.** A body is read under byte, depth, token and number-length limits and a timeout, and
decoded only through the codec of a declared response case; a response beyond a limit is a `limit-failure`, never a
partially decoded value. Redirects are never followed.

**Credentials belong to the application.** A generated client asks its credential provider for them on every call and keeps
none. During SSR the Nuxt module forwards only allowlisted headers, keeps credentials out of the payload, and keys request
identities with a server-side secret when credentials were forwarded. The Explorer holds credentials in the page's memory
only and names them as placeholders in the code it shows.

**The Explorer is closed by default.** Outside Development its routes need both `TisiliaOptions.AllowProduction` and an
`AuthorizationPolicy`, and the page is served with a content security policy that allows only its own scripts, styles and
connections, `nosniff`, `no-referrer` and `no-store`.

## Nuxt dependency advisories

As of 2026-11-16, Nuxt 4.5 and 4.6 bring one upstream advisory into a consumer installation, and no release that the
dependency ranges admit fixes it:

- [GHSA-g3hv-75wg-m249](https://github.com/advisories/GHSA-g3hv-75wg-m249): `minimatch` before 11.0.1 can spend
  excessive CPU time and memory preprocessing a pattern crafted to multiply its parts. 11.0.1 fixes it.

Nitro uses `minimatch` only while it builds the server, through `glob` when `@vercel/nft` traces the server's
dependencies and through an archive package that zips the output for the Azure Functions preset. The patterns come from
Nitro and from the code it traces. `@vercel/nft` 1.11.2 and later depend on a `glob` that brings the fixed major
version, but the current release of the archive package does not accept it yet. Do not use these dependencies with
patterns from untrusted input.

In this repository `minimatch` comes only through the Nuxt module's development dependency on Nuxt and is not in
Tisilia's packages, so its Dependabot alert is dismissed. `minimatch` has not been patched or ignored, and a Nuxt
consumer's own installation still contains an affected release. A workspace audit with `--omit=dev` excludes the
workspace's Nuxt development dependency and does not describe a Nuxt consumer's full installation. Run `npm audit` in
the consuming application too.

A fix that the dependency ranges already admit reaches a fresh install, but an existing `package-lock.json` keeps the
version it recorded: run `npm update <package>` or `npm audit fix` in the consuming application, then build again.
`nitropack` 2.13.6 is such a fix. It fixes three advisories in the server's proxy and redirect route rules and its cached
handlers ([GHSA-rhhp-7488-3qwf](https://github.com/nitrojs/nitro/security/advisories/GHSA-rhhp-7488-3qwf),
[GHSA-39r7-wfcx-99xg](https://github.com/nitrojs/nitro/security/advisories/GHSA-39r7-wfcx-99xg) and
[GHSA-pqxc-r4h5-jc95](https://github.com/nitrojs/nitro/security/advisories/GHSA-pqxc-r4h5-jc95)), and every Nuxt 4.5
and 4.6 release admits it. `@vercel/nft` 1.11.2 is another. `glob` 9.0.0 through 14.0.1 keeps reading directories
after a walk is cancelled through its `signal` option
([GHSA-3g3m-2fjr-6pw4](https://github.com/advisories/GHSA-3g3m-2fjr-6pw4)). `@vercel/nft`, which Nitro uses to trace
the server's dependencies, depends on `glob` 14.0.6 or later from 1.11.2 on, and `nitropack` 2.13.4 through 2.13.6
admit those releases. Earlier 1.x releases depend on the 13.x line, to which `glob` 13.0.7 carries the 14.0.2 fix, but
the advisory lists only 14.0.2 as patched, so `npm audit` and Dependabot keep reporting 13.0.7: take `glob` 14 with
`npm update @vercel/nft`. `nitropack` 2.13.5 also brings `glob` 10 through the Azure Functions preset's archive
packages, and no `glob` 10.x release has the fix; 2.13.6 no longer brings it.
`node-forge` 1.4.3 is another. Through 1.4.0 it accepts malformed RSA signature encodings
([GHSA-86w9-cpqp-85rv](https://github.com/advisories/GHSA-86w9-cpqp-85rv)), and 1.4.1 fixed the other advisories its
maintainers published for those versions on 2026-10-27. Through 1.4.2 its Ed25519 verification accepts small-order
public keys, a server can block a `forge.http` client with one large response header, and a malicious server can crash
a `forge.tls` client in two ways ([GHSA-6889-vf6x-f3g5](https://github.com/advisories/GHSA-6889-vf6x-f3g5),
[GHSA-6cv4-p8m7-4r25](https://github.com/advisories/GHSA-6cv4-p8m7-4r25),
[GHSA-j8rw-jp99-435v](https://github.com/advisories/GHSA-j8rw-jp99-435v) and
[GHSA-w4xr-hqv8-ch44](https://github.com/advisories/GHSA-w4xr-hqv8-ch44), published on 2026-11-10); 1.4.3 fixes them.
It comes through `listhen`, which uses it for local certificate handling and which Nitro depends on (Nuxt 4.5's CLI
does too), and every `listhen` 1.x release admits 1.4.3.
Nuxt DevTools 3.4.3 and later are another: every Nuxt 4.5 and 4.6 release admits them, and they no longer depend on
`simple-git`, which carried [GHSA-858h-whjf-mvg5](https://github.com/advisories/GHSA-858h-whjf-mvg5),
[GHSA-g4wm-2vf7-vfgr](https://github.com/advisories/GHSA-g4wm-2vf7-vfgr),
[GHSA-x6jw-m9v5-85vh](https://github.com/advisories/GHSA-x6jw-m9v5-85vh) and, through `@simple-git/argv-parser`,
[GHSA-v5rq-49vh-5v5c](https://github.com/advisories/GHSA-v5rq-49vh-5v5c). A Nuxt server build copies the server's runtime
dependencies into `.output`; Vue's compiler, for example, brings `source-map-js`, fixed in 1.2.2 for
[GHSA-68fv-2mgg-jv7q](https://github.com/advisories/GHSA-68fv-2mgg-jv7q).

Advisories in Nuxt itself are Nuxt's to fix. Nuxt 4.6.4 is a security release that fixes eight of
[them](https://github.com/nuxt/nuxt/security/advisories) and requires `nitropack` 2.13.6, and the Nuxt module accepts it.

For a Node deployment, build in a trusted environment and deploy only Nuxt's standalone `.output` directory, starting
`node .output/server/index.mjs` as described in the [Nuxt deployment guide](https://nuxt.com/docs/4.x/getting-started/deployment).
Check the actual output's dependencies before deployment; custom modules and presets can change what is included.
This reduces exposure to development tooling but does not fix the vulnerable packages in the build environment.

## Not a compliance guarantee

Using Tisilia does not make an application compliant with any data protection regime or with your own policies. What an API
exposes, who may call it, and how its data is stored remain the responsibility of the application.
