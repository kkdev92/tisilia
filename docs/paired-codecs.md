# Paired codecs

A type that your own `JsonConverter` serializes needs a paired codec: the converter keeps running on the server, a
TypeScript module of yours runs in the client, and a registration says what values the type has and what the converter
reads and writes. Export refuses a custom converter without one (SV05); it never falls back to `any`. The
[additional codecs](additional-codecs.md) and [portable codecs](portable.md) are paired codecs written for you; this page
is about writing one.

## The converter in effect

A registration names the CLR type and the converter System.Text.Json uses for it in the JSON options the endpoint is
served with:

- the first converter in the options that can convert the type,
- else the type's `[JsonConverter]` attribute,
- else System.Text.Json's own converter, which needs no registration.

When that converter is a `JsonConverterFactory`, the registration names the factory, not the closed converter the factory
creates for the type: the factory is what the options hold. A converter in a property's `[JsonConverter]` attribute
binds that property only, with a registration of its own.

## A registration

An identifier that wraps a Guid, written as the Guid's text:

```csharp
builder.Services.AddTisilia(o =>
{
    o.Codecs.AddPaired(new PairedCodecRegistration
    {
        ClrType = typeof(OrderId),
        ConverterType = typeof(OrderIdJsonConverter),
        ModelId = "shop.OrderId",
        TsName = "OrderId",
        // the value the client holds: a brand over the builtin Guid, so an OrderId is not a CustomerId in TypeScript
        Shape = f => new BrandShape { BrandId = "shop.OrderId", Base = f.Builder.Scalar("guid") },
        ModuleId = "shop.ids",
        ModuleVersion = "1.0.0",
        License = "MIT",
        Artifacts =
        [
            // the assembly holding the converter, and the module, at their paths below the content root (the contract pins both by digest)
            new(ArtifactTarget.Dotnet, "bin/Release/net10.0/Shop.Api.dll", Bytes: () => File.ReadAllBytes(typeof(OrderIdJsonConverter).Assembly.Location)),
            new(ArtifactTarget.Browser, "modules/shop.ids/ids.js", Bytes: () => File.ReadAllBytes(Path.Combine(contentRoot, "modules/shop.ids/ids.js"))),
            new(ArtifactTarget.Node, "modules/shop.ids/ids.js", Bytes: () => File.ReadAllBytes(Path.Combine(contentRoot, "modules/shop.ids/ids.js"))),
        ],
        DotnetConverterExport = nameof(OrderIdJsonConverter),
        DomainRuleExport = "idDomainRule",
        DotnetOracleExport = "IdOracle",
        TypescriptOracleExport = "idOracle",
        Request = new PairedDirection(f => f.StringWire("shop.OrderId.read", WireDirection.ServerRead, Builtins.Grammar("guid")), "idRequestEncode", "idValidate", "idRequestInput"),
        Response = new PairedDirection(f => f.StringWire("shop.OrderId.write", WireDirection.ServerWrite, Builtins.Grammar("guid")), "idResponseDecode", "idValidate"),
        ConverterContext = [],
        SettingsText = "OrderIdJsonConverter: the Guid in its D form",
        // route, query and header values: the type's TryParse reads the same text the codec writes
        ParameterGrammarId = Builtins.Grammar("guid"),
        // for tisilia conformance: the CLR value as the domain the client holds, and back
        Project = value => DomainAst.String(((OrderId)value).Value.ToString("D", CultureInfo.InvariantCulture)),
        Construct = ast => ast is JsonStringValue text ? new OrderId(Guid.ParseExact(text.Value, "D")) : throw new FormatException("an OrderId is a Guid"),
        Oracle = (a, b) => a.Equals(b),
    });
});
```

The module is an ES module that imports nothing, with one export per role the registration names — the shape of each is
in [the TypeScript side of the additional codecs](../src/backend/Tisilia.Generator/Additional/tisilia-additional.d.ts):

```js
const pattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

function fail(code, path, message) {
  const e = new Error(message);
  e.name = "CodecError";
  e.code = code;
  e.path = path;
  return e;
}

export function idDomainRule(value, context) {
  if (typeof value !== "string" || !pattern.test(value)) throw fail("grammar", context.path, "an id is a lowercase Guid");
  return value;
}
export const idValidate = { validateDomain: idDomainRule };
export const idRequestEncode = { encodeRequest: (value, context) => ({ kind: "string", value: idDomainRule(value, context) }) };
export const idResponseDecode = {
  decodeResponse(wire, context) {
    if (wire.kind !== "string") throw fail("type-mismatch", context.path, "an id is a JSON string");
    return idDomainRule(wire.value, context);
  },
};
export const idRequestInput = { parseRequestInput: (input, context) => idDomainRule(typeof input === "string" ? input.trim() : input.value, context) };
export function idOracle(a, b) {
  return a === b;
}
```

`tisilia generate` imports the module from the path the contract records, after checking its digest. Keep it LF in every
checkout: a `.gitattributes` with `* -text` next to it does that (`export` warns about a module with CRLF line endings).

Ids, export names and the TypeScript name have the contract's forms: an id is a letter followed by letters, digits and
`_ . : @ / -`, an export name is a JavaScript identifier. Export reports a registration that breaks them with the field
that holds the name (SV01).

## Converter factories

One factory often serializes many types by one rule — every identifier type of an application, every enum. Register the
factory once, with a callback that returns the registration of one closed type:

```csharp
o.Codecs.AddPairedFactory<IdJsonConverterFactory>(type => new PairedCodecRegistration
{
    ClrType = type,
    ConverterType = typeof(IdJsonConverterFactory),
    ModelId = "shop." + type.Name,
    TsName = type.Name,
    Shape = f => new BrandShape { BrandId = "shop." + type.Name, Base = f.Builder.Scalar("guid") },
    // … the module, exports, wires and settings of the registration above, shared by every id type
});
```

Export asks the callback only for the closed types its endpoints use where the factory is the converter in effect, once
per type, so the callback never needs a list of them; a type that only route, query or header values reach is asked for
there. A callback that returns `null` for a type, returns a registration for another type or another converter, or
throws, leaves that type unbound, and SV05 says which of these happened (an exception by its type only: application code
may put anything in its message). A type registered with `AddPaired` keeps that registration.

Each closed type is a binding of its own — kind `factory`, with the closed type among the settings it fixes — so the
registrations of one factory can share a settings text, a module and its exports. What differs per type and the client
needs to know (the names of an enum, say) goes in `ConverterContext`: non-secret entries reach the module as
`context.context.get(name)`. Conformance compares each type with the `Oracle` of its own registration.

## An enum held by name

When the converter reads and writes an enum by name alone — it writes a name for every value and refuses numbers — the client
can hold the names instead of the integers. Give the registration an enum shape whose domain is its names, and let the
projection go through the names:

```csharp
Shape = _ => new EnumShape
{
    UnderlyingPrimitiveId = Builtins.Scalar("int32"),
    Flags = false,
    AllowUndefinedInteger = false,
    Members =
    [
        new EnumMember { Name = nameof(Finish.Matte), Value = "0", SerializedName = "matte" },
        new EnumMember { Name = nameof(Finish.HighGloss), Value = "1", SerializedName = "high-gloss" },
    ],
    // the client holds each member's serialized name (its name when it has none), the text the converter writes
    NameDomain = true,
},
Request = new PairedDirection(f => f.StringWire("shop.Finish.read", WireDirection.ServerRead, Builtins.Grammar("enum-name")), "finishRequestEncode", "finishValidate", "finishRequestInput"),
Response = new PairedDirection(f => f.StringWire("shop.Finish.write", WireDirection.ServerWrite, Builtins.Grammar("enum-name")), "finishResponseDecode", "finishValidate"),
// for tisilia conformance: the name the client holds, and back
Project = value => DomainAst.String(FinishNames.Of((Finish)value)),
Construct = ast => ast is JsonStringValue text ? FinishNames.Parse(text.Value) : throw new FormatException("a finish is one of its names"),
```

The generated type is the union of the names, with a const object that maps each member to its name:

```ts
export type Finish = "matte" | "high-gloss";
export const Finish = { Matte: "matte", HighGloss: "high-gloss" } as const;
```

The module's exports take and return the names, and conformance builds its cases from them. A name domain is neither
`[Flags]` nor open to integers no member defines, and no name stands for two values (SV49). An enum that System.Text.Json's
own `JsonStringEnumConverter` writes needs no registration: declare its domain with `TisiliaOptions.Enums` (see
[What the types say](getting-started.md#6-what-the-types-say)).

## Keeping the contract stable

The contract records the digest of the assembly named as the `Dotnet` artifact, so any change to that assembly changes the
contract, and the client has to be generated again. That includes a new build of a project it references, since its PDB
records the identity of each referenced assembly, and a build of the same source in which the compiler numbered the
delegate types of route handlers with optional parameters differently. Keep the converters and their registrations in a
small assembly of their own rather than in the API project, referencing as few of your projects as it can, and build it and
those projects reproducibly (see [Configure and generate](getting-started.md#4-configure-and-generate) in the
getting-started guide). To make it independent of the projects it references, build it without a PDB
(`<DebugType>none</DebugType>` in its project file): the PDB is where the identity of the referenced assemblies is recorded,
and an embedded one (`embedded`) records it too. Stack traces through that assembly then show no file names or line numbers.
