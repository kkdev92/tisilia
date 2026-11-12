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

As of 2026-11-12, Nuxt 4.5 and 4.6 bring two upstream advisories into a consumer installation, and no release that the
dependency ranges admit fixes them:

- [GHSA-gcqx-w48m-w2h4](https://github.com/advisories/GHSA-gcqx-w48m-w2h4): `deepmerge` 4.2.0 through 4.3.1
  lets a merged source object inject properties into the prototype of the object it returns, and no release fixes it.
  It comes through `@rollup/plugin-node-resolve`, which Nitro uses when it bundles the server, and that plugin merges only
  its own default options with it. Do not use this dependency to merge untrusted objects.
- [GHSA-g3hv-75wg-m249](https://github.com/advisories/GHSA-g3hv-75wg-m249): `minimatch` before 11.0.1 can spend
  excessive CPU time and memory preprocessing a pattern crafted to multiply its parts. 11.0.1 fixes it.

Nitro uses `glob` and `minimatch` only while it builds the server: `@vercel/nft` traces the server's dependencies with
them, and an archive package zips the output for the Azure Functions preset. The patterns come from Nitro and from the
code it traces, and no cancellation signal is passed. The `minimatch` fix is a new major version, which the current
releases of these packages do not accept yet. Do not use these dependencies with patterns or signals from untrusted
input.

In this repository `deepmerge`, `minimatch` and `glob` come only through the Nuxt module's development dependency on
Nuxt and are not in Tisilia's packages, so their Dependabot alerts are dismissed. `deepmerge` and `minimatch` have not
been patched or ignored, and a Nuxt consumer's own installation still contains them. A workspace audit with `--omit=dev`
excludes the workspace's Nuxt development dependency and does not describe a Nuxt consumer's full installation. Run
`npm audit` in the consuming application too.

A fix that the dependency ranges already admit reaches a fresh install, but an existing `package-lock.json` keeps the
version it recorded: run `npm update <package>` or `npm audit fix` in the consuming application, then build again.
`nitropack` 2.13.6 is such a fix. It fixes three advisories in the server's proxy and redirect route rules and its cached
handlers ([GHSA-rhhp-7488-3qwf](https://github.com/nitrojs/nitro/security/advisories/GHSA-rhhp-7488-3qwf),
[GHSA-39r7-wfcx-99xg](https://github.com/nitrojs/nitro/security/advisories/GHSA-39r7-wfcx-99xg) and
[GHSA-pqxc-r4h5-jc95](https://github.com/nitrojs/nitro/security/advisories/GHSA-pqxc-r4h5-jc95)), and every Nuxt 4.5
and 4.6 release admits it. `glob` 13.0.7 is another. `glob` 9.0.0 through 14.0.1 keeps reading directories after a walk
is cancelled through its `signal` option ([GHSA-3g3m-2fjr-6pw4](https://github.com/advisories/GHSA-3g3m-2fjr-6pw4)),
and 13.0.7 carries the 14.0.2 fix to the 13.x line that `@vercel/nft` depends on. `nitropack` 2.13.5 also brings `glob`
10 through the Azure Functions preset's archive packages, and no `glob` 10.x release has the fix; 2.13.6 no longer
brings it. The advisory still lists only 14.0.2 as patched, so `npm audit` and Dependabot keep reporting 13.0.7: take
it with `npm update glob`. `node-forge` 1.4.3 is another. Through 1.4.0 it accepts malformed RSA signature encodings
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
