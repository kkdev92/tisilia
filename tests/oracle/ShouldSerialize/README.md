# ShouldSerialize

A program that shows what decides whether System.Text.Json writes a member: its `[JsonIgnore(Condition = …)]`, the options'
`DefaultIgnoreCondition`, or a `ShouldSerialize` predicate that a type info modifier sets or removes. A member's
`[JsonIgnore]` condition is a predicate of System.Text.Json's own, in reflection and in source-generated metadata alike. A
predicate the modifier sets replaces it and the options' condition, which applies only to a member without a predicate; one
the modifier removes leaves a member with a `[JsonIgnore]` condition always written, and a member without one to the options.
Export holds a member's write condition to this (`ShouldSerializeTests`, and `PresenceWrapperTests` for a presence wrapper's
absent state). It is a .NET 10 file-based app run from the repository root; the root `Directory.Build.props` applies to it,
so the file overrides what it needs with `#:property`.

```text
dotnet run tests/oracle/ShouldSerialize/observe.cs
```

Nothing here is built by any project or shipped in a package.
