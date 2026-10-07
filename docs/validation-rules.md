# Validation rules SV01–SV54

Every contract Tisilia reads or writes is checked against these rules: the export never writes a contract that breaks
one, and `validate`, `generate`, `check` and `conformance` refuse it. A diagnostic names its rule in brackets
(`error TIS1302 [SV05]: …`). The table says what each rule checks and where it is enforced; paths are relative to
`src/backend/`, and `SemanticValidator*` is `Tisilia.Generator/Validation/SemanticValidator{,.Types,.Http,.Profiles}.cs`.

Three rules are enforced by construction or at runtime rather than by a contract-level check, and say so below: SV15
(the exporter keys codecs by CLR type, converter instance settings and profile), SV35 and SV47 (the Nuxt runtime checks
every envelope against the contract before it hydrates).

| rule | what it checks | enforced in |
|---|---|---|
| SV01 | format/version exact, control numbers are safe integers, unknown fields rejected | `TisiliaSchemas`, `ContractLoader`, `Jcs`, `SemanticValidator` |
| SV02 | ids unique across registries, module dependency/file names too; one documentation entry per target | `SemanticValidator`, `ContractIndex`, `CodecBindingCollection` (duplicate model id) |
| SV03 | id references resolve by kind, WireRef direction matches; documentation entries target ids of the contract | `SemanticValidator*`, `ClrTypeMapper` (DateTime application converters without bindings, unknown converters, an application converter on a builtin scalar type, .NET framework types without a converter — BigInteger, IPAddress, Rune, IPNetwork, Index, Range, Complex, TimeZoneInfo and CultureInfo point at the converters Tisilia ships —, `JsonValue` under System.Text.Json's own converter (an object or array is a 500) points at `JsonValueJsonConverter`, `Nullable<T>` dictionary keys, `[JsonExtensionData] JsonObject` in responses while System.Text.Json writes it as invalid JSON — dotnet/runtime#97225, observed at startup), `PortableProjectLoader` |
| SV04 | `TypeUse.codecId` belongs to `TypeUse.typeId` | `SemanticValidator.Types/.Profiles` |
| SV05 | request positions need request capability, responses response, map keys key capability | `SemanticValidator.Types/.Profiles`, `ClrTypeMapper` (unbound converters; a type-level registration naming a converter System.Text.Json does not use there — BigInteger without `AddTisiliaAdditionalConverters()` — with the fix; map keys of module types only through a registration's key capability — none for `Uri`, whose `Uri.Equals` merges keys; types System.Text.Json cannot read — read-only/abstract collections, interfaces, abstract classes, types without a usable constructor — have no request capability) |
| SV06 | semanticNullable / wire null branch / nullBehavior consistency | `SemanticValidator.Types/.Profiles` |
| SV07 | child TypeUses of array/map/union/brand/extension checked recursively | `SemanticValidator.Profiles` (usage graph) |
| SV08 | map keys non-null, comparer resolves, collision policy reject | `SemanticValidator.Types/.Profiles` |
| SV09 | only productive finite recursion | `SemanticValidator.Profiles`, `PortableProjectLoader` (`CheckRecursion`) |
| SV10 | property names unique per domain/wire, case/duplicate rules resolve | `SemanticValidator.Types` |
| SV11 | extension capture needs child codecs, known-field collisions rejected | `SemanticValidator.Types` (discriminator literal exemption) |
| SV12 | token-union has ≥2 branches, distinct tokens matching each branch root | `SemanticValidator.Types` |
| SV13 | tagged-union tags unique, branches objects, discriminator position/type/value | `SemanticValidator.Types`, `ClrTypeMapper` (polymorphism; a concrete base that is not one of its own derived types is refused for responses, since System.Text.Json writes its instances without a discriminator), portable generators |
| SV14 | codec dependencies declared for every reachable child | `SemanticValidator.Types`, `ContractBuilder` (nullable wrappers) |
| SV15 | one CLR type with different meanings per property/profile never shares a TS model/codec | `ClrTypeMapper.MapPaired` (binding/codec ids per converter instance settings and profile), `MapCore` (member-level converter ids); consequences checked by SV16, SV24, SV52 |
| SV16 | profile/binding ids resolve, every option recorded, body root profile in codec scope, custom resolvers / contract modifiers named by a resolver binding (`TisiliaOptions.Resolvers`); the source-generated contexts ASP.NET Core inserts itself (`ProblemDetailsJsonContext`, `OpenApiJsonSchemaContext` of `AddOpenApi`, the Identity endpoints' and bearer token contexts) are builtin resolvers, and the application's own source-generated contexts are `tisilia.resolver.stj-source-generated@0.1` bindings that record the context, its generation options and its fast-path types | `ProfileContext` (`RegisterResolverBinding`, `FrameworkResolvers`, `SourceGeneratedResolverBinding`), `SemanticValidator.Http/.Profiles` |
| SV17 | maxDepthRaw 0 → effective 64, else equal | `SemanticValidator.Profiles` |
| SV18 | no `Always` default ignore, no IgnoreNullValues with non-Never | `ProfileContext`, `SemanticValidator.Profiles` |
| SV19 | normalized behaviors need a projection, opaque paths are G1 only | `ClrTypeMapper` (behavior registration, G1 downgrade; an optional nullable request member that the server constructs as non-null when omitted — initializer, constructor default or constructor logic, observed by reading `{}` — is an error until an initializer/constructor behavior is registered; a hand-written setter is a warning; a response member System.Text.Json writes but never reads — get-only, `WhenReading` — makes the owner's response G1 with a warning until a getter behavior is registered), `SemanticValidator.Profiles`, `SuiteBuilder` (G1 → not applicable; members with a registered initializer/constructor behavior keep their constructed value in the request oracle) |
| SV20 | HandleNull, property overrides, factory closed types, nested options closure; Preserve allowed only for builtin scalar JSON, custom reference handlers refused | `SemanticValidator.Profiles`, `ProfileContext` |
| SV21 | equivalence domain/direction matches capability, round trip needs a bridge | `SemanticValidator.Types`, `PortableProjectLoader` |
| SV22 | projection source/target match the usage, preserved/notPreserved consistent | `SemanticValidator.Types`, portable generators |
| SV23 | module exports resolve with role/target, builtin ids whitelisted | `SemanticValidator*`, `TsGenerator` |
| SV24 | one claim per binding scope, settings/context never hidden | `SemanticValidator*`, `CodecBindingCollection`, `BehaviorBindingCollection`, `ContractBuilder.FindEquivalentBinding` |
| SV25 | comparer meaning explicit | `SemanticValidator.Types` |
| SV26 | GET/HEAD have no request body | `SemanticValidator.Http` |
| SV27 | HEAD / 204 / 205 / 304 bodyless, 1xx excluded | `SemanticValidator.Http` |
| SV28 | status/media/caseId unique, no content sniffing | `SemanticValidator.Http` |
| SV29 | JSON / text / binary / SSE / none distinguished; raw uploads need one concrete non-form media type; binary responses have their own adapter and no fabricated JSON profile | `SemanticValidator.Http/.Types`, exporter `ResponseMetadata`: known file markers with explicit status/media support buffered or incremental finite downloads; byte[] JSON stays JSON; ambiguous JSON/file declarations fail; SSE (minimal API endpoints only) requires SseItem<T> metadata and its own text/JSON data contract; raw byte/object events remain diagnosed |
| SV30 | parameter location/binder match, path variables complete; flat form fields have unique safe names, primitive values or finite file parts | `TisiliaContractExporter` (DateTime parameters use the mixed wire by default; fixed Local declarations are refused because ASP.NET Core binds them with `AdjustToUniversal`; module types with a parameter grammar bind through codec binders, `ContractBuilder.CodecBinder`; enum parameters bind through enum binders, `ContractBuilder.EnumBinder` — C# member names or integers for minimal APIs, defined values only (`enum-name` grammar) for MVC; `ParameterInfo.DefaultValue` = `DBNull` is no default; absent repeated parameters bind as empty collections, so they are optional), `SemanticValidator.Http` |
| SV31 | nullPolicy literal ↔ nullLiteral, required vs omit/null | `SemanticValidator.Http` |
| SV32 | no naive path+query concatenation, unsafe scheme/header/CRLF rejected | `SemanticValidator.Http` |
| SV33 | redirect statuses rejected as typed cases | `SemanticValidator.Http` |
| SV34 | pipeline/result/profile closure resolves | `TisiliaContractExporter` (warning for `Results<…>` members that describe no response — UnauthorizedHttpResult, ProblemHttpResult — unless the endpoint declares further statuses; error for a minimal API handler returning `IResult` without response metadata, which ApiExplorer lists as a bare 200, and for `TypedResults.Json`/`JsonResult`, whose options are chosen at run time; warning for a handler that returns nothing but receives `HttpContext`/`HttpResponse`), `SemanticValidator.Http` |
| SV35 | hydration per case; binary is server-only even with an unsafe hand-authored descriptor | `SemanticValidator.Http`, runtime `hydration.ts`, Nuxt composable/core: SSR serverResult retained, safe envelope carries no file, hydration does not refetch it |
| SV36 | redaction selectors resolve, safe fallback for invalid bodies | `SemanticValidator.Http`, explorer `redactTree`/`wirePreview` |
| SV37 | limits are positive safe integers, same bound in decoder and HTTP stream | `Pipeline` (config), runtime `limits.ts`, `transport.ts` |
| SV38 | output path containment, symlinks, reserved names, case collisions | `OwnedOutput`, `PortableCodegen`, `TsGenerator`, `Commands.Explorer` |
| SV39 | TypeScript ≥ 6, moduleMode / ESM / `.js` imports | `Commands.Conformance` (runner matrix), generator |
| SV40 | portable imports/digests/ids, no project cycles | `PortableProjectLoader` |
| SV41 | portable read token-unions reach one domain with a unique writer | `PortableProjectLoader` |
| SV42 | no token-union in response programs, op/representation supported | `PortableProjectLoader`, `PortableModel`, `PortableCSharpGenerator` |
| SV43 | semanticHash / profile fingerprints recomputed and equal | `SemanticValidator`, `Commands.Conformance` (the runner-mode application must export the contract's semanticHash: an environment-bound codec whose accepted ids differ — TimeZoneInfo/CultureInfo under another globalization mode — is refused before any case runs) |
| SV44 | module artifacts match manifest target/export, no self-referential hash; byte-exact, a mismatch that is only line endings says so, export warns about a CRLF script or a source-revision assembly | `ClrTypeMapper`, `Pipeline`, `TsGenerator`, `SemanticValidator`, `Commands.Conformance/.Explorer`, `LineEndings`, `TisiliaContractExporter.ReportUnstableArtifacts` |
| SV45 | evidence scope/hash/closure/environment/context/limits/issuer match, failed = skipped = 0 | `EvidenceValidator`, `Pipeline`, `Commands` |
| SV46 | qualified coverage only from valid codec evidence; zero-codec operations are codec-not-applicable / HTTP-unobserved | `EvidenceValidator`, `Pipeline`; explicit qualified-only filter retains exclusion reasons |
| SV47 | envelope case/status/media/body-kind/hash match the contract, scope/request correspondence | `src/frontend/nuxt/src/runtime/core.ts` (`envelopeMatchesScope`, `hydrate`) |
| SV48 | generation manifest lists owned files only with real digests, no self-hash; a CRLF checkout of owned files is neither stale nor an edit (`contractArtifactDigest` reads the contract with CRLF as LF) | `OwnedOutput`, `Pipeline` |
| SV49 | enum base type and member values in range, names unique, aliases kept | `ClrTypeMapper` (an enum without members is refused: the schema's enum shape requires at least one), `SemanticValidator.Types` |
| SV50 | literal values scalar, discriminator required and matching | `ClrTypeMapper`, `PortableCSharpGenerator`, `SemanticValidator.Types` |
| SV51 | auxiliary bindings (grammar, name-matching, duplicates, editor, naming, encoder, resolver, acceptance) resolve by kind | `SemanticValidator` |
| SV52 | nameMappings ids/kinds/names/paths match the output, no same-scope name collisions | `ClrTypeMapper` (`UniqueTsName`; enum names qualified by form, declaring type or namespace and paired brand names numbered when another model has the name; type names ASCII by the schema, other identifier characters as code points), `ContractBuilder` (scalar models do not take names), `TsGenerator` (runtime imports aliased in the models file when a model has their name, codec names numbered after the builtin scalars, enum const keys never `__proto__` setters, collisions with operation/client exports and duplicate argument keys reported), `TsNames` |
| SV53 | closure record recomputed with the exact fields and ordering | `EvidenceValidator` |
| SV54 | runner action arity/outputs and session/request correlation, request-identity record consistency | `RunnerProtocol`, `TisiliaRunnerHostedService`, `SemanticValidator`, TypeScript runner |

For contract 0.1, SV30 also validates the resolved route plan, stable parameter references and agreement with the display route.
Optional/default segments and supported complex separators are accepted; ambiguous omission, unknown outbound transforms and
unsafe values remain diagnostics. Runtime checks the final URL origin and base-path segment boundary. SV29 accepts explicit
finite-file metadata as binary without a JSON profile. These rules do not claim all proxies or streaming APIs are supported.

Form object groups require child fields and indexed repetition for collections. SV30 rejects overlapping expanded wire names,
multiple root collections sharing the same index space, misplaced empty `wireName` overrides and scalar fields with children.
Generated and interpreted encoders reject empty indexed collections, empty object items and undeclared nested fields before sending.
