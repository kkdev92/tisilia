using Microsoft.CodeAnalysis;

namespace Tisilia.AspNetCore.SourceGenerator;

/// <summary>
/// Records, for each operation a project registers with Tisilia, the responses its handler returns, which the exporter uses when
/// the endpoint declares no response types: a minimal API handler that returns <c>Results.Ok(value)</c>, or an MVC action that
/// returns <c>IActionResult</c>. It reads only those handlers, and generates one internal class.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ResponseInferenceGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var chained = context.SyntaxProvider.CreateSyntaxProvider(
            static (node, _) => OperationReader.IsChainCandidate(node),
            static (syntax, cancellationToken) => OperationReader.FromChain(syntax, cancellationToken));
        var attributed = context.SyntaxProvider.ForAttributeWithMetadataName(
            "Tisilia.TisiliaOperationAttribute",
            static (node, _) => OperationReader.IsAttributeTarget(node),
            static (syntax, cancellationToken) => OperationReader.FromAttribute(syntax, cancellationToken));
        // the project directory, which the SDK makes visible to analyzers: source positions are recorded relative to it
        var projectDirectory = context.AnalyzerConfigOptionsProvider.Select(static (options, _) =>
            options.GlobalOptions.TryGetValue("build_property.ProjectDir", out var directory) ? directory : null);

        context.RegisterSourceOutput(
            chained.Collect().Combine(attributed.Collect()).Combine(projectDirectory),
            static (output, input) => Emitter.Emit(output, input.Left.Left, input.Left.Right, input.Right));
    }
}
