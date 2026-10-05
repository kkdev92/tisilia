# AdditionalTypes

Programs that hold the additional codecs (`tisilia-additional`) to .NET 10 itself: what System.Text.Json and the server-side
converters do with IPNetwork, Index, Range, Complex, JsonValue, TimeZoneInfo and CultureInfo, and whether the module judges
every text the way the converters do. They are .NET 10 file-based apps run from the repository root; the root
`Directory.Build.props` applies to them, so each file overrides what it needs with `#:property`.

```text
dotnet run tests/oracle/AdditionalTypes/observe.cs           # STJ's defaults for Complex/Index/Range/IPNetwork, IPNetwork Parse/ToString, the host's zones and cultures
dotnet run tests/oracle/AdditionalTypes/jsonvalue.cs         # STJ's own JsonValueConverter throws InvalidOperationException on an object or array (a 500 in ASP.NET Core)
dotnet run tests/oracle/AdditionalTypes/environment-ids.cs   # the ids the environment-bound codecs accept, and their round trip (ICU)
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 dotnet run tests/oracle/AdditionalTypes/environment-ids.cs   # the same under invariant globalization

npm run build                                                 # complex-decode.mjs uses the runtime's dist
node tests/oracle/AdditionalTypes/make-corpus.mjs <dir>/corpus.json               # a corpus judged by the module itself
dotnet run tests/oracle/AdditionalTypes/check-corpus.cs -- <dir>/corpus.json      # read and write it with the real converters; count the disagreements
node tests/oracle/AdditionalTypes/complex-decode.mjs <dir>/complex-out.json       # read what the server wrote for Complex with the module, bit for bit
```

`<dir>` is a working directory outside the repository (the corpus runs to several megabytes). The random values come from
mulberry32 with a fixed seed, so the corpus stays the same as long as the module does. Nothing here is built by any project
or shipped in a package.
