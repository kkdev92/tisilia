# ShouldSerialize

A program that shows which decides whether System.Text.Json writes a member: `[JsonIgnore(Condition = WhenWritingDefault)]`
on the member, the options' `DefaultIgnoreCondition`, or a `ShouldSerialize` predicate that a type info modifier sets. A
predicate the modifier sets replaces the member's ignore condition and the options' one; without one, either leaves the
default value out. Export holds a presence wrapper's absent state to this (`PresenceWrapperTests`). It is a .NET 10
file-based app run from the repository root; the root `Directory.Build.props` applies to it, so the file overrides what it
needs with `#:property`.

```text
dotnet run tests/oracle/ShouldSerialize/observe.cs
```

Nothing here is built by any project or shipped in a package.
