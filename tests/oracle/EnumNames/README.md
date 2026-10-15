# EnumNames

A program that holds an enum held by name (`TisiliaOptions.Enums`) to .NET 10 itself: what `JsonStringEnumConverter` reads
and writes with integers allowed and refused, as values and as dictionary keys; that it reads the names it writes as written
and a member's C# name in any case, except a member a `[JsonStringEnumMemberName]` names; what it writes for two members of
one value and for two values a naming policy writes as one name; and what `Enum.TryParse`, which minimal APIs bind enum
route and query values with, accepts. It is a .NET 10 file-based app run from the repository root; the root
`Directory.Build.props` applies to it, so the file overrides what it needs with `#:property`.

```text
dotnet run tests/oracle/EnumNames/observe.cs
```

Nothing here is built by any project or shipped in a package.
