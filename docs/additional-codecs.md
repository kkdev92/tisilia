# Additional codecs and declared DateTime wires

.NET types outside the builtin scalar set are used through certified additional codecs, never rounded to a string or a
number silently. Tisilia ships one paired module, `tisilia-additional`, for fifteen of them,
and optionally narrows the builtin mixed `DateTime` wire. Without additional-codec registrations the exporter
refuses those fifteen types with a diagnostic that names the fix; builtin `DateTime` needs no registration.

## Setting up

```csharp
using Tisilia.AspNetCore.Codecs;

// the converters System.Text.Json lacks (BigInteger, IPAddress, Rune, IPNetwork, Index, Range, Complex, TimeZoneInfo,
// CultureInfo) and one for JsonValue that answers an object or array with 400 — Tisilia never changes JSON options by itself
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.AddTisiliaAdditionalConverters());
builder.Services.AddControllers().AddJsonOptions(o => o.JsonSerializerOptions.AddTisiliaAdditionalConverters());

builder.Services.AddTisilia(o =>
{
    o.Codecs.AddAdditionalCodecs(builder.Environment.ContentRootPath);   // optional 2nd argument: a prefix for the TS type names
    o.DateTimes.Default = DateTimeWire.Utc;                              // DateTime values hold UTC unless a member says otherwise
    o.DateTimes.Add(typeof(Meeting), nameof(Meeting.LocalStart), DateTimeWire.Unspecified);
});
```

```text
tisilia codec install-additional --project <content root>   # writes modules/tisilia-additional/{tisilia-additional.js,.d.ts,.gitattributes}
```

The exported contract records the module's artifacts (the browser/node script at `modules/tisilia-additional/…` relative
to the contract, and `Tisilia.AspNetCore.dll` in the application's build output) with their digests; `generate` and
`conformance` check the installed copy against them (SV44) and name the install command when it is missing or stale.
An existing file with other content is never replaced without `--force`; a copy that differs only in its line endings (a CRLF
checkout) is replaced without it. The `.gitattributes` (`* -text`, written when the folder has none) keeps git from converting
the files on checkout — Git for Windows would, by default — so commit it with them.

## The types

| .NET type | server converter | wire | TypeScript domain |
|---|---|---|---|
| `Int128`, `UInt128` | System.Text.Json's | number token; a string under `WriteAsString`, also read from a string under `AllowReadingFromString` | canonical decimal string (`Int128` brand) |
| `BigInteger` | `BigIntegerJsonConverter` | number token, canonical integer, at most 4096 characters both ways | canonical decimal string |
| `Half` | System.Text.Json's | shortest round-trip number (`6E-08`, `65500`, `-0`); strings / `NaN` `Infinity` `-Infinity` under number handling | a number exactly representable in binary16 (`halfOf(x)` rounds, ties to even) |
| `Uri` | System.Text.Json's | `Uri.OriginalString` | string; requests are RFC 3986 references .NET's parser keeps, responses any string |
| `Version` | System.Text.Json's | `Version.ToString()` | `major.minor[.build[.revision]]` without leading zeros |
| `IPAddress` | `IPAddressJsonConverter` | the canonical `IPAddress.ToString()` text only | the same text (`::ffff:1.2.3.4`, `fe80::1%3`) |
| `Rune` | `RuneJsonConverter` | a string of one Unicode scalar | a string of one scalar |
| `IPNetwork` | `IPNetworkJsonConverter` | the canonical `IPNetwork.ToString()` CIDR text only: a canonical address without host bits, a prefix without leading zeros | the same text (`10.0.0.0/8`, `2001:db8::/32`, `fe80::%2/64`) |
| `Index` | `IndexJsonConverter` | C# index syntax: `"3"`, `"^3"` (0 … 2147483647, no leading zeros) | the same text |
| `Range` | `RangeJsonConverter` | C# range syntax with both ends: `"1..^2"`, `"0..^0"` | the same text |
| `Complex` | `ComplexJsonConverter` | `{"real": number, "imaginary": number}`, finite parts, exactly those two members | `{ real: number; imaginary: number }` (an object model, `-0` kept) |
| `JsonValue` (`System.Text.Json.Nodes`) | `JsonValueJsonConverter` | one JSON string, number (its lexeme kept) or boolean; an object or array is a 400 | the JSON AST of that token (`JsonScalar`) |
| `TimeZoneInfo` | `TimeZoneInfoJsonConverter` | `TimeZoneInfo.Id`; reads an id `FindSystemTimeZoneById` resolves to a zone of exactly that id | string; requests: the ids the exporting server accepts (binding context `zones`) |
| `CultureInfo` | `CultureInfoJsonConverter` | `CultureInfo.Name` (`""` is the invariant culture); reads predefined culture names of exactly that spelling | string; requests: the names the exporting server accepts (binding context `cultures`) |

Each scalar-like type is a brand in the generated models (`export type Int128 = string & {…}`), so a value is written
with a cast (`"42" as Int128`) and checked by the codec at the boundary; `Complex` is an object model. The module's
`.d.ts` also exports helpers (`halfOf`, `isHalf`, `formatHalf`, `ipv6Text`, `timeZoneIds(context)`, `cultureNames(context)`).

- **Number handling** is part of the binding for `Int128`, `UInt128` and `Half` (System.Text.Json applies it to its own
  number converters): every effective handling gets its own codec, wires and the context entry `numbers` (`r`, `w`, `n`).
  The client always writes number tokens, which the server reads under every handling. `NaN`/`±Infinity` are `Half`
  values only under `AllowNamedFloatingPointLiterals`. `Complex` parts are plain finite doubles under every handling.
- **Dictionary keys**: `Int128`, `UInt128`, `BigInteger`, `Half`, `Version`, `IPAddress`, `Rune`, `IPNetwork`, `Index`
  and `Range`. Not `Uri` (`Dictionary<Uri, T>` compares keys with `Uri.Equals`, which merges keys that differ in case,
  escaping or fragment; the TypeScript map would not see that collision), nor `Complex`, `JsonValue`, `TimeZoneInfo` and
  `CultureInfo` (their converters have no property-name form). `Half` keys are finite; the server treats `-0` and `0` as
  one key.
- **Route, query and header parameters**: `Int128`, `UInt128`, `BigInteger`, `Half`, `Uri`, `Version`, `IPAddress` and
  `IPNetwork`, for minimal API endpoints and controller actions. The client writes the request codec's canonical text,
  which ASP.NET Core's `TryParse` (invariant culture), the type's `TypeConverter` or `Uri.TryCreate` reads unchanged.
  Absent repeated parameters bind as empty arrays.
- **Minimal API form values**: the same types, written as the same text. A root value or array binds through the type's
  `TryParse`; members of form models and other collections bind through the form mapper, which reads a value through
  `IParsable<T>` or, for `Uri`, `Uri.TryCreate`. `Version` does not implement `IParsable<T>`, so the form mapper reads it as
  a model: it works as a root `[FromForm] Version` or `Version[]` parameter, and is diagnosed as a member. An empty value
  is refused before sending, as it is for a parameter. MVC reads form values with the request culture, so an MVC form value
  of these types is text its parser reads, sent unchecked with a warning (SV30). `Rune`, `Index`, `Range`,
  `JsonValue`, `TimeZoneInfo` and `CultureInfo` have no `TryParse`, so ASP.NET Core cannot bind them as parameters;
  `Complex` has one (`<real; imaginary>`), which no codec binder models: such a parameter is sent as text its `TryParse`
  reads, unchecked, with a warning (SV30).
- **Uri requests** follow .NET's parser, not only RFC 3986: hosts are DNS names (labels of `[A-Za-z0-9_-]` starting with a
  letter or digit), IPv4 or bracketed IPv6 addresses; no one-letter scheme (`C:` is a drive); `file` URIs without
  userinfo, port or `:` in the path (a leading drive excepted); `mailto:local@domain` with no `/` in the local part.
- **IPNetwork** text is canonical in both directions. `IPNetwork.TryParse` itself is more lenient — it clears host bits
  (`10.0.0.1/8` → `10.0.0.0/8`), drops a scope that the masking changed and reads prefixes with leading zeros — so the
  converter refuses any text that does not survive `Parse` → `ToString` unchanged, and the module refuses the same texts.
- **JsonValue** is a node holding one token. System.Text.Json's own `JsonValueConverter` answers a JSON object or array
  in that position with `InvalidOperationException` ("The element cannot be an object or array."), which ASP.NET Core
  turns into a 500; the exporter therefore binds the codec to `JsonValueJsonConverter` only and names it when the
  application still uses the default one. JSON `null` stays a null reference (`JsonScalar | null`).

## Environment-bound: TimeZoneInfo and CultureInfo

Which zone ids and culture names a server accepts depends on where it runs: the time zone database, ICU data, and
`InvariantGlobalization` (with it, `CultureInfo` knows only the invariant culture `""`). The registrations compute the
set the exporting server accepts — every id with `FindSystemTimeZoneById(id).Id == id` (system zones, `UTC`, and the
IANA/Windows ids the runtime converts), every name with `GetCultureInfo(name, predefinedOnly: true).Name == name` — and
record it as the binding context entries `zones` and `cultures` (JSON arrays; part of the codec binding, so a server
with another set produces another contract and other evidence). The TypeScript codec checks requests against
that list (`domain-rule` before sending) and accepts any string in responses, since the server writes whatever id or
name its value carries (a custom zone, an alias it resolved). Neither type can be a dictionary key or a parameter.
Conformance follows the same rule: when the runner-mode application accepts another set than the contract records (an
export under invariant globalization run under ICU), it exports another semanticHash and conformance stops with SV43
instead of producing evidence for an environment the contract does not describe. Keep these types out of a contract
that is committed and checked on several machines unless their environments match.

## Enum route, query and header parameters

Not an additional codec, but configured nowhere else: enum parameters bind through `enumBinder`.
Minimal APIs bind them with `Enum.TryParse<T>` — case-sensitive C# member names (not the JSON names of
`JsonStringEnumMemberName`), integers including undefined ones, comma-separated flags; MVC's `EnumTypeModelBinder`
accepts the same texts case-insensitively but refuses values that are not defined (for `[Flags]`, not a combination of
defined flags). The client writes a defined value as its C# member name and any other value as its integer; for MVC
actions the contract binder is `tisilia.grammar.enum-name@0.1` and the client refuses undefined values before sending.

## DateTime

The default builtin `datetime` handles every `Kind`: `DateTimeUtc | DateTimeUnspecified | DateTimeLocalWire`.
`parseDateTime` / `formatDateTime` keep the suffix and 100 ns ticks without a JS Date or client-zone conversion.
`TisiliaOptions.DateTimes.Default` and member exceptions (`Add(type, member, wire)`) optionally narrow the domain:
`Utc` and `Unspecified` select `datetime-utc` / `datetime-unspecified`; `Local` selects `datetime-local-wire`;
`Mixed` selects the union. Local JSON expectations depend on the server's zone.
HTTP parameters accept the default union, but a fixed Local declaration is invalid (SV30): ASP.NET Core's
`DateTimeStyles.AdjustToUniversal` converts offset input to UTC. The server converts DateTime dictionary keys with an
offset to its own zone, which can make keys the client writes differently one key: `TisiliaOptions.DateTimes.ServerTimeZone`
puts the zone's offsets in the contract, and the client refuses exactly those keys. See
[the DateTime guide](getting-started.md#6-what-the-types-say).

## How it is checked

The module is held to .NET 10 itself rather than to a description of it. The programs in `tests/oracle/AdditionalTypes`
observe what System.Text.Json and the converters do, build a corpus from the module's own verdicts and read and write it
back through the real converters, so any text the module and the server judge differently shows up as a mismatch
(`tests/oracle/AdditionalTypes/README.md` says how to run them). The runtime tests pin the edges
(`src/frontend/runtime/test/additional.test.ts`), and the module's samples and invalid samples feed the standard
conformance suite like those of every other codec.
