# DotnetStjOracle

A program that shows what .NET 10's System.Text.Json actually does with the values the runtime has to read and write
exactly. Copy `Program.Part1.cs.txt` and `Program.Part2.cs.txt` into one `Program.cs` of a `net10.0` console project with
`InvariantGlobalization=true` and run it with `dotnet run -c Release`. Some rows (a `DateTime` of Kind `Local`) depend on the
machine's time zone.

The files end in `.cs.txt` so that no project builds them, and nothing here is shipped in a package.
