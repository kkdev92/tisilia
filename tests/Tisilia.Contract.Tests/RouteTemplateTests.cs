using Microsoft.AspNetCore.Routing.Patterns;
using Tisilia.Generator.Diagnostics;
using Tisilia.Generator.Validation;
using Xunit;

namespace Tisilia.Contract.Tests;

/// <summary>
/// The validator reads route templates as ASP.NET Core's RoutePatternParser does: compared with <see cref="RoutePatternFactory.Parse(string)"/>
/// on templates routing accepts — escaped braces, constraints holding ':' '=' '?' or ')' and defaults after a constraint included.
/// </summary>
public class RouteTemplateTests
{
    [Theory]
    [InlineData("/users/{id}")]
    [InlineData("/users/{id:guid}")]
    [InlineData("/users/{id:int:min(1)}")]
    [InlineData("/users/{id?}")]
    [InlineData("/users/{id:int?}")]
    [InlineData("/users/{page=1}")]
    [InlineData("/users/{page:int=1}")]
    [InlineData("/files/{*path}")]
    [InlineData("/files/{**path}")]
    [InlineData("/files/{filename}.{ext?}")]
    [InlineData("/a{b}c{d}")]
    [InlineData("/braces/{{literal}}/{id:int}")]
    [InlineData(@"/ssn/{message:regex(^\d{{3}}-\d{{2}}-\d{{4}}$)}")]
    [InlineData(@"/v/{v:regex(^\d+(\.\d+)?$)}")]
    [InlineData(@"/b/{b:regex(^[A-Za-z0-9+/]*={{0,2}}$)}")]
    [InlineData(@"/l/{l:regex((?=\w)\w+)}")]
    [InlineData(@"/d/{d:regex(^a$)=a}")]
    [InlineData(@"/c/{c:regex(([}}])\w+)}")]
    [InlineData(@"/m/{m:regex(^a:b$):minlength(2)}")]
    public void Route_parameters_are_read_as_aspnetcore_routing_reads_them(string template)
    {
        var pattern = RoutePatternFactory.Parse(template);
        var expected = pattern.Parameters.Select(p => (p.Name, p.IsOptional, p.IsCatchAll, HasDefault: p.Default is not null)).ToList();
        var actual = HttpRules.ParseRouteVariables(template).Select(v => (v.Name, v.Optional, v.CatchAll, v.HasDefault)).ToList();
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("/users/{id:guid}/glob*/x")]
    [InlineData(@"/users/{id:regex(^x*/y$)}")]
    public void A_route_holding_the_end_of_a_comment_keeps_the_generated_code_intact(string route)
    {
        // routing accepts '*' in literal text and '/' inside a constraint; the operation's doc comment would end at "*/"
        var root = SampleContracts.UsersApiJson();
        RouteTestContracts.SetRoute(root["operations"]![0]!, route);
        var bag = new DiagnosticBag();
        var index = SemanticValidator.Validate(ContractLoader.Load(root.ToJsonString(TisiliaJson.Options), bag)!, bag, verifyHashes: false)!;
        Assert.DoesNotContain(bag.Items, d => d.Severity == DiagnosticSeverity.Error);
        var options = new Generator.TypeScript.TsGenerationOptions { ModuleMode = Documents.ModuleMode.NodeNext, GeneratorVersion = "test", ModuleImportResolver = (_, artifact) => "./" + artifact.Path };
        var operations = Generator.TypeScript.TsGenerator.Generate(index, options, bag, out _)!.Single(f => f.Path == "operations/index.ts").Content;
        // a comment — one line or a documented block — closes only at the end of its last line, never inside the route
        var commentLines = operations.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("/**", StringComparison.Ordinal) || l.StartsWith('*'));
        foreach (var line in commentLines.Where(l => l.Contains("*/", StringComparison.Ordinal)))
        {
            Assert.Equal(line.Length - 2, line.IndexOf("*/", StringComparison.Ordinal));
        }

        Assert.Contains(Generator.TypeScript.TsNames.CommentText(route), operations, StringComparison.Ordinal);

        Assert.Contains("route: " + Generator.TypeScript.TsNames.Quote(route) + ",", operations, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"/v/{v:regex(^\d+(\.\d+)?$)}/x", "/v/p/x")]
    [InlineData("/braces/{{literal}}/{id:int}", "/braces/{literal}/p")]
    [InlineData(@"/u/{u:regex(^https?://x#y$)}", "/u/p")]
    public void Literal_text_is_what_remains_outside_the_parameters(string template, string literal)
    {
        Assert.Equal(literal, HttpRules.RouteLiteralText(template));
    }
}
