# Conformance runner and evidence

`tisilia conformance` observes that the server's System.Text.Json behaviour and the generated TypeScript client agree
for the operations of a contract, and writes a `tisilia.conformance-evidence` record. Coverage in the generation
manifest becomes `qualified` only through such a record (SV46): a declaration never qualifies anything.

This qualification covers codec behavior only. Routes, HTTP binding, binary transport, authentication, CORS and proxies remain
HTTP-unobserved by this runner. A binary/bodyless operation with no codec closure has `codec-not-applicable` and `http-unobserved`
coverage reasons, never a fictitious passed test. Development generation includes it; `qualified-only` still explicitly filters
unqualified operations and explains an empty qualified selection. Mixed JSON/binary operations qualify only their codec scope.
The contract, Codec ABI and runner protocol use version 0.1; the standard suite uses version 0.1.0. Evidence must match the contract hash,
closure, artifacts, issuer and environment. It cannot be migrated by replacing version strings.

## Grades

Every equivalence in a contract says how much of a value survives the trip between the server and the client:

| grade | what it claims |
|---|---|
| G1 | The server accepts what the client writes from the declared domain, and the client decodes what the server writes. A value may come back different. |
| G2 | A value means the same on both sides, under the equivalence's projection and normalization: what the client writes, the server reads as an equal value, and what the server writes, the client decodes as an equal value — except the aspects listed in `notPreserved`. |
| G3 | G2, and the lexeme, scale or offset the equivalence names is kept as well. |

The exporter grades each type from what the server does with it — a set that drops duplicates, a key policy that renames
keys, a member the server writes but never reads, an opaque behavior all make it G1 — and the suite below tests each claim
it can: round trips and discrimination for G2 and G3, negatives and domain validation for every grade.

## Runner protocol

Two isolated processes answer `tisilia.runner-message` records (UTF-8 JSON Lines, one record per line, 16 MiB per
record by default, `limits.maxBodyBytes` when configured):

| runner | process | adapters |
|---|---|---|
| dotnet | the application itself, started with `TISILIA_RUNNER=1` (`Tisilia.AspNetCore.Conformance.TisiliaRunnerHostedService`) | codec ids → CLR type + the profile's serializer options recorded by the exporter (`RunnerAdapterTable`); paired registrations supply `Project`/`Construct`/`Oracle` delegates |
| node | `node --input-type=module -e …` in the generated client directory, `@kkdev92/tisilia-runtime/conformance/runner` | codec ids → the generated `createRegistry()`; module oracles through the generated `registry.js` |

Rules both runners enforce: session id from `TISILIA_RUNNER_SESSION`, one response per request, `compare`, `ts-project`
and `dotnet-project` take two inputs and every other action one, every response carries exactly one output, failures use the fixed code set
(`invalid-input`, `contract`, `codec`, `limit`, `timeout`, `cancelled`, `internal`) with an id-shaped `safeMessageId`
and a path. The orchestrator treats a wrong session/request id, an extra or duplicate record, a malformed record,
an exit or a timeout as a runner failure that fails every remaining case. Logs go to stderr only (the console logger
is redirected in runner mode).

Before serving, each runner writes an environment report (`TISILIA_RUNNER_ENV`): its own digest (`runnerDigest`),
the runtime matrix (.NET / ASP.NET Core / STJ / OS / architecture, Node / TypeScript), non-secret context (culture,
time zone) and, for .NET, the application assembly as an application artifact and the exported `semanticHash`,
which must equal the contract file's.

## Projected domain AST

Both runners project public values with the same rules (`DomainAst` in C#, `DomainBridge` in TypeScript):
integers and decimals are number lexemes (decimal scale preserved, no exponent, no sign on zero), binary floats use the
.NET shortest round-trip text (`NaN`/`Infinity`/`-Infinity` as strings), guids are lowercase `D`, bytes base64,
dates/times/durations the System.Text.Json writer text, enums their integer value, objects carry members in model
order (absent members omitted, captured extension data under `extensions`), maps use the key codec's canonical text,
unions carry the discriminator first. Values are rebuilt from the AST by trusted factories — the registration's
`Construct` for paired types, and for everything else a copy of the profile options that reads canonical number/named
float forms and swaps paired converters for the domain factory. That factory is not the HTTP body reader.

## Standard suite

`SuiteBuilder` derives cases from the qualification closure alone, deterministically from a SplitMix64 seed:

- `response-round-trip:<type>` — `dotnet-write` → `ts-decode-response` → `compare` on both runners (`p = W(c)`, `D(p) ≡ c`)
- `request-round-trip:<type>` — `ts-encode-request` → `dotnet-read` → `compare` on both runners (`c = R(E(q)) ≡ q`)
- `key-round-trip:<type>` — key codecs in both directions (only for profiles without a `DictionaryKeyPolicy`)
- `negative-wire:<type>` — an invalid corpus derived from the wire shape that the decoder must reject with a codec failure
- `domain-validation:<type>` — `validate-domain` accepts a valid value and rejects every other AST kind / range violation
- `oracle-discrimination:<type>` — two different values must not compare equal (the oracle has discriminating power)

`datetime-local-wire` and the Local variant of `datetime` have expectations that depend on the environment: System.Text.Json
reads an offset form as `DateTimeOffset.LocalDateTime` (Kind Local in the server's zone) and writes that zone's offset
back. The conformance command therefore builds the suite for the zone the .NET runner reports (`SuiteOptions.TimeZoneId`
= the evidence context's `dotnet.timeZone`, IANA or Windows id), computes the local-wire round trips with
`TimeZoneInfo.ConvertTime`, skips instants whose local time is ambiguous there (DST fall-back), and the claim is valid
for that recorded context only. `datetime-utc` (`Z`) and `datetime-unspecified` (no suffix) round-trip unchanged; the
forms of the other kinds are server-write negatives for those narrowed declarations. The default `datetime` union accepts
all three forms. Its corpus interleaves the three Kinds and normalizes offset-bearing values and dictionary keys only;
UTC and unspecified values remain unchanged. Multi-key DateTime request corpora use UTC/unspecified keys; Local singleton
keys are also covered. Unknown server-zone collisions are refused by the client rather than silently overwriting an entry.

The suite also derives these from the contract rather than from the code:

- **Converter instances**. A paired registration may describe the settings of the instance in effect
  (`PairedCodecRegistration.DescribeInstance`); an instance whose settings differ from the registration's defaults gets its
  own binding and codec ids (`demo.Money.codec.<8 hex of the settings digest>`), its own equivalences and its own cases, and
  is bound only to the profiles that use it. The binding's non-secret context (e.g. `scale=2`) reaches the TypeScript
  module through the codec context — the runner passes it with every action, and the generated client and the contract
  interpreter wrap module calls with the same entries (`withContext`).
- **Behaviors**. A populated member (`JsonObjectCreationHandling.Populate`) constructs a value that differs
  from the received JSON, so the member must be registered as a behavior; a normalized effect names a projection with a
  .NET and a TypeScript implementation (module exports with role `projection`). Request round trips of such a type are
  `request-round-trip-projected:<type>` cases: the expected value is projected by both runners (`ts-project`,
  `dotnet-project`, inputs = domain AST and the member path, `*` for array items), the two projections must agree, and only
  then is the server's value compared with it. An absent populated member keeps its initialized value, so the suite does
  not read it as `null` the way it does for other optional nullable members. Write-side effects (should-serialize,
  getter) project the response round trip the same way. An **opaque** effect makes the owner type's equivalences G1: the
  suite generates no round-trip or discrimination cases for a G1 equivalence and lists it under `notApplicable` in the
  suite and the report (negatives and domain validation still run), so the missing claim is visible, never a hidden skip.
  A registered **initializer** or **constructor** behavior is read the same way as Populate: an omitted member keeps the
  value the server constructs (the projection describes it) instead of `null`.
- **Grades over the closure**. An equivalence cannot claim more than the codecs it depends on in the same
  direction: a G1 dependency — a set or stack (the server's read drops duplicates or reorders), keys renamed by
  `DictionaryKeyPolicy` on write, an opaque behavior — makes every equivalence containing it G1, with the dependency's
  `notPreserved` aspects. The suite reads the grade, so a value containing a set has no request round-trip claim.
- **Members the server writes but never reads**. A get-only member (an initializer such as
  ASP.NET Core's `AccessTokenResponse.TokenType { get; } = "Bearer"`, or a computed getter) or one with
  `[JsonIgnore(Condition = WhenReading)]` has no setter, init accessor or constructor parameter that System.Text.Json
  uses, so the runner cannot build a server value holding a generated value of it. Unless a behavior describes it (a
  getter registration for the member, or a normalized or opaque one for the type — an identity registration such as a
  validating callback does not), the exporter warns (SV19) and makes the owner's response equivalence G1 with
  `notPreserved` `member-value:<name>`; a registered getter (normalized, with a projection that computes the value) keeps
  G2 and projects the round trip.
- **Values the runner builds through the server's read.** A response round trip constructs the server value by reading
  the generated value with System.Text.Json. Collections whose read drops duplicates or reorders get at most one item, and
  the discriminator of a polymorphic variant is the literal on the variant's wire (the server reads variants only through
  the base type). The .NET runner projects `Memory<T>`/`ReadOnlyMemory<T>` and `IAsyncEnumerable<T>` as arrays and writes
  `IAsyncEnumerable<T>` with the asynchronous serializer, as ASP.NET Core does.

Boundary corpora come first (int64 around 2^53 and the limits, decimal scale 28 and the 96-bit limits, float
`-0`/`5e-324`/`1e17`, dates at the range limits and ±14:00 offsets, 100 ns ticks, strings with escapes, emoji and
control characters), then seeded random values. Expectations apply the profile's write semantics (`WhenWritingNull`
etc. drop members) and the read semantics (a missing nullable member reads as `null`). `compare` uses the equivalence's
registered oracle; the builtin structural/numeric oracles walk the domain model and delegate each member to the
member type's own equivalence, so a paired member such as `Money` is compared by its module oracle (`notPreserved`
scale beyond 4 decimals is honoured by the oracle, not by the suite).

Generated values cannot anticipate module domain rules; candidates that the registered TypeScript domain rule rejects
are dropped before the run (builtin codecs are never filtered). Binding context entries whose name matches a string
member (`currency=JPY`) fix that member's generated value. The `requiredTests` categories are derived from the closure
before filtering, so a codec without a surviving sample cannot be claimed.

The brands of the additional codec module (`tisilia-additional.*`) are narrower than their base scalars (an Int128 is not
any string), so they have their own sample source (`AdditionalModule.Sample`): the edges of each domain first — the
Int128/UInt128 limits, a 4096-character BigInteger, the largest, smallest subnormal and negative-zero binary16 values,
IPv6 forms with an embedded IPv4 part and a scope, a supplementary-plane Rune — then seeded values, together with
invalid samples for domain validation (out of range, leading zeros, uncompressed IPv6, two scalars …). Brands over a
primitive get key round-trip cases like primitives do. The wire-negative heuristics (fractions and overflow for integer
grammars, guid/date/base64 corpora) apply to builtin grammar ids only; a module grammar binding gets the token negatives.

## Evidence and qualification (SV45/SV46/SV53)

The record contains scope (operation ids, capabilities, equivalence ids of the closure), the runtime matrix, module
artifacts, application artifacts (the application assembly and every compiled `.js` of the generated client), context,
suite id/version/JCS digest/seed/runner digest/protocol, counts, required tests, limits and
`claim: observed-conformance`. `closureDigest` is the SHA-256 of the canonical (RFC 8785) closure record built from
exactly these inputs; the verdict is
`passed` only with `failed = 0`, `skipped = 0`, `passed > 0` and at least one passed case in every required category.

`generate`/`check --evidence a.json,b.json --trusted-issuer id` re-check each record: schema, `semanticHash`,
recomputed `closureDigest` and sorted scope, verdict and counts, required categories of the closure, trusted issuer,
and that the recorded node/browser artifacts still exist under the generation output with the recorded digests
(evidence is bound to the observed client build; a rebuilt client or another output directory does not qualify).
Invalid records produce warnings and `unqualified` coverage with reason codes (`untrusted-issuer`,
`evidence-hash-mismatch`, `evidence-closure-mismatch`, `evidence-artifact-mismatch`, `evidence-failed`,
`evidence-skipped`, `missing-required-tests`, …). `generate` and `check` print the resulting coverage in their
`--format json` result (`check` only plans; it never writes). With `coveragePolicy: qualified-only` only qualified
operations are published, and generation fails when none qualifies.

## Running it

```text
dotnet tisilia conformance --config tisilia.json --project src/Api --allow-execute-adapters \
    --output src/Api/tisilia.evidence.json --report tisilia.conformance-report.json --issuer local
dotnet tisilia generate --config tisilia.json --evidence src/Api/tisilia.evidence.json --trusted-issuer local
```

`conformance` runs the client as it was compiled — `index.js` and `registry.js` must be in the output directory, so run
`tsc` after `generate` — under Node as the TypeScript runner, builds and starts the application as the .NET runner (its
startup code runs, as it does for `export`), runs the suite and writes the evidence. `generate --evidence` then marks the
operations the evidence covers as qualified.

Exit codes: 0 verdict passed, 5 verdict failed, 6 a runner could not be started, 7 without `--allow-execute-adapters`.
