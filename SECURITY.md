# Security Policy

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

Nuxt 4.5.2 currently brings these unresolved upstream advisories into a consumer installation:

- [GHSA-vfj7-8cjw-p6xm](https://github.com/advisories/GHSA-vfj7-8cjw-p6xm): `braces` through 3.0.3
  can exhaust the stack on deeply nested patterns. Nitro uses it through `globby` / `fast-glob` / `micromatch`
  for build-time file matching. Build only trusted source and configuration; never pass request data into glob patterns.
- [GHSA-86w9-cpqp-85rv](https://github.com/advisories/GHSA-86w9-cpqp-85rv): `node-forge` through 1.4.0
  accepts malformed RSA signature encodings. Nuxt's CLI uses it through `listhen` for local certificate handling.
  Do not use the development server or `nuxt preview` as a production server, and do not use this dependency to verify
  untrusted signatures or certificates.
- [GHSA-858h-whjf-mvg5](https://github.com/advisories/GHSA-858h-whjf-mvg5),
  [GHSA-g4wm-2vf7-vfgr](https://github.com/advisories/GHSA-g4wm-2vf7-vfgr),
  [GHSA-x6jw-m9v5-85vh](https://github.com/advisories/GHSA-x6jw-m9v5-85vh) and
  [GHSA-v5rq-49vh-5v5c](https://github.com/advisories/GHSA-v5rq-49vh-5v5c): `simple-git` through 3.36.0 and
  `@simple-git/argv-parser` before 2.0.1 let git options, git configuration and editor variables past simple-git's guard
  against unsafe operations, up to command execution. Nuxt DevTools 3.4.2 (Nuxt 4.5.2 requires `^3.4.1`) depends on
  `simple-git` `^3.36.0` and runs `git branch`, `rev-parse` and `status` in the project's root when it names a build
  analysis. A `node-server` build's `.output` contains neither DevTools nor `simple-git`. Run the development server only
  in a checkout you trust, and turn DevTools off (`devtools: { enabled: false }` in `nuxt.config`) where you do not use it.

As of 2026-10-06, `braces` and `node-forge` have no published fix, and the fixes for `simple-git` (4.0.0 and 4.0.1) and
`@simple-git/argv-parser` (2.0.1) are majors that Nuxt 4.5.2's dependencies do not admit. In this repository they come
only through the Nuxt module's development dependency on Nuxt and none of them is in Tisilia's packages, so their
Dependabot alerts are dismissed as tolerable risk. The dependencies have not been patched or ignored, and a Nuxt
consumer's own installation still contains them. A workspace audit with `--omit=dev` excludes the workspace's Nuxt
development dependency and does not describe a Nuxt consumer's full installation. Run `npm audit` in the consuming
application too.

A fix that the dependency ranges already admit reaches a fresh install, but an existing `package-lock.json` keeps the
version it recorded: run `npm update <package>` or `npm audit fix` in the consuming application, then build again. A Nuxt
server build copies the server's runtime dependencies into `.output`; Vue's compiler, for example, brings `source-map-js`,
fixed in 1.2.2 for [GHSA-68fv-2mgg-jv7q](https://github.com/advisories/GHSA-68fv-2mgg-jv7q).

For a Node deployment, build in a trusted environment and deploy only Nuxt's standalone `.output` directory, starting
`node .output/server/index.mjs` as described in the [Nuxt deployment guide](https://nuxt.com/docs/4.x/getting-started/deployment).
Check the actual output's dependencies before deployment; custom modules and presets can change what is included.
This reduces exposure to development tooling but does not fix the vulnerable packages in the build environment.

## Not a compliance guarantee

Using Tisilia does not make an application compliant with any data protection regime or with your own policies. What an API
exposes, who may call it, and how its data is stored remain the responsibility of the application.
