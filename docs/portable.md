# Portable codec DSL

`tisilia codec generate --project <tisilia.portable.json> [--content-root <dir>] [--force]` turns a portable project
(the manifest, its `tisilia.portable-definition` files and digest-checked imports) into:

| output | content |
|---|---|
| `<output.csharp>/Domain.g.cs` | one C# record per object model (`PMoney(decimal Amount, string Currency, string? Note = null)`), one abstract record per union model with the variant records deriving from it (`Circle(decimal Radius, string? Label = null) : Shape`; the discriminator is not a member), each decorated with `[JsonConverter(typeof(<Definition>PortableConverter))]` so System.Text.Json applies the generated converter wherever the type is serialized (type-level attribute: options converters such as the runner's domain factory still take precedence). Optional members are `null` when absent; because a record has no absent state, a nullable optional member is written and projected as `null`, a non-nullable optional member is omitted when `null` |
| `<output.csharp>/<Definition>PortableConverter.g.cs` | a `JsonConverter<T>` per definition: `Read` follows the request program with `Utf8JsonReader` (token unions dispatch on the token, tagged unions look the discriminator up on a copy of the reader and dispatch on the tag, `unknownMembers` reject/ignore, duplicate and missing members are `JsonException`s, float lexemes beyond the binary range are rejected), `Write` follows the response program (unions switch on the runtime type and write the discriminator first); plus the conformance projection/factory (`ProjectDomain`/`ConstructDomain`) |
| `<output.csharp>/Registrations.g.cs` | `PairedCodecRegistration`s with `Origin = portable`, builtin structural oracles, the wires derived from the programs (`<model>.read` / `<model>.write`), the module artifacts (assembly + TypeScript file) and `Dependencies` (the CLR types of referenced definitions, registered first by the exporter) — `foreach (var r in DemoPortableRegistrations.All(contentRoot)) o.Codecs.AddPaired(r);` |
| `<output.csharp>/<project>.codec-manifest.json` | the `tisilia.codec-manifest` module declaration (artifact digests, export roles, models, equivalences incl. the synthesized ones, projections) |
| `<output.typescript>/<project>.portable.js` + `.d.ts` | one self-contained ES module (no imports, so the Explorer can serve it raw) exporting per definition `<name>DomainRule`, `<name>Validate`, `<name>RequestEncode` (canonical writer), `<name>ResponseDecode`, `<name>RequestInput` (lossless JSON AST → domain through the request program). Its prelude carries exact parsers/writers for every builtin scalar, ported function by function from the runtime primitives (decimal and float with exact binary rounding, 100 ns tick calendars, TimeSpan `c`, strict base64, char) |
| `<output.typescript>/.gitattributes` | written when the folder has none: `/<project>.portable.js -text` and the `.d.ts`, so that git does not convert the module's line endings on checkout (the contract records its bytes; Git for Windows checks text out with CRLF by default) |
| `<output.csharp>/tisilia.portable-ownership.json` | file digests of the generation; existing files not owned by it are never overwritten, edited owned files only with `--force` (a CRLF checkout of a generated file is not an edit) |

Nothing is executed: projections are C# static classes named by the projection's `dotnetImplementation` export
(`ToClr`/`ToDomain`), and the generated source is only proven when the application compiles. The
identity projection means the CLR type *is* the generated domain record.

## Programs, models and definitions

Every op is generated: `scalar` (all builtin scalars, `native` or `string` representation for number
scalars), `ref`, `nullable`, `array`, `object`, `token-union` (request side only) and `tagged-union`.

- A **tagged union** needs a union model whose variants are object models; each variant model declares the
  discriminator as its first property (a required `std.string`), exactly as the contract represents polymorphic
  objects. The branch programs do not declare the discriminator: the union reads it to dispatch and writes it from
  the runtime type. On the wire each variant carries the discriminator as a required literal (SV13/SV50).
- An object or union model that programs describe **inline** (a nested object, a union variant, a nested union) is a
  codec of the module like any other: the loader synthesizes a definition for it from the object/tagged-union op that
  produces it (one program per direction per model; differing programs are rejected), so the contract carries a
  truthful module-implemented codec instead of a structural one. Nested models therefore get their own converter,
  registration and TypeScript exports; the enclosing converters delegate to them.
- `ref` positions, including `nullable(ref …)` and arrays of refs, call the referenced definition's converter. Recursion
  through refs must be **productive**: every cycle passes a nullable, an array element or an optional member;
  a required direct self reference is rejected.

## Validation (SV40–SV42)

- SV40: project imports must exist with the recorded digest; import cycles at project level are rejected; definition
  and model ids are unique across the import closure; the builtin set is `tisilia.builtins@0.1`; refs resolve;
  non-productive recursion is rejected.
- SV41: a token union appears only in request programs, its branches must not overlap by token, nested token unions
  are rejected, every branch must reach the position's domain, and the definition must have a response program with
  the same domain and projection (its writer becomes the TypeScript request encoder).
- SV42: response programs contain no token union; scalar ids are builtin scalars; the `string` representation applies
  to number scalars only; every op corresponds to the declared model shape (object members ↔ properties incl.
  presence/nullability, array element, tagged-union tags ↔ union variants with the discriminator rules above, every
  variant has a branch, refs ↔ the referenced definition's domain); a variant model belongs to one union.

## What the generator does not do

- Enum, map and brand models are not portable models: no op produces them (use a paired module).
- `datetime-local-wire` (the Invoice's `localWire`) is read by the generated C# as `DateTimeOffset.LocalDateTime` — the
  server's own zone — and written back with that zone's offset, so it is only certified under a fixed server time zone:
  the conformance suite computes its expectations for the zone the .NET runner reports (`dotnet.timeZone` in the
  evidence context). `datetime-utc` and `datetime-unspecified` round-trip unchanged.

## Example

`tests/fixtures/portable/tisilia.portable.json` with `{pmoney,shape,invoice,tree}.definition.json`, the project the tests
generate from:

- `PMoney`: the request reads `amount` as a JSON number **or** a decimal string (token union), the response always
  writes the string form; the TypeScript encoder therefore writes strings.
- `Shape`: a tagged union (`kind` = `circle` | `rect`) with a string-represented decimal and an array member.
- `Invoice`: inline nested objects (`customer`, `lines[]`), a plain and a nullable ref to `PMoney`, a ref to `Shape`, an
  inline union (`payment`: `card` | `cash`) and every scalar family (guid, dates, times, duration, float32/float64,
  bytes, char, int64, decimal).
- `Tree`: productive recursion (`left: nullable ref`, `children: array of ref`).

An application registers the generated codecs with
`foreach (var r in DemoPortableRegistrations.All(contentRoot)) o.Codecs.AddPaired(r);`, and the export, the generated client
and the conformance suite then cover them like any paired codec (origin `portable` in the contract).
