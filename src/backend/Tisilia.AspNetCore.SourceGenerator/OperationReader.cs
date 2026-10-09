using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Tisilia.AspNetCore.SourceGenerator;

/// <summary>
/// Finds the operations a project registers, by the constant id it gives them: <c>WithTisiliaOperation("id")</c> on the builder a
/// Map call returns, and <c>[TisiliaOperation("id")]</c> on a controller action, a method or a lambda.
/// </summary>
internal static class OperationReader
{
    private static readonly HashSet<string> MapMethods = new(StringComparer.Ordinal)
    {
        "Map", "MapGet", "MapPost", "MapPut", "MapDelete", "MapPatch", "MapMethods", "MapFallback",
    };

    public static bool IsChainCandidate(SyntaxNode node) =>
        node is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "WithTisiliaOperation" } };

    public static bool IsAttributeTarget(SyntaxNode node) =>
        node is MethodDeclarationSyntax or LocalFunctionStatementSyntax or LambdaExpressionSyntax;

    /// <summary>
    /// <c>WithTisiliaOperation("id")</c>, followed back through the builder it is called on to the Map call that created it: each
    /// call in between returns the builder it was called on (WithName, RequireAuthorization, …).
    /// </summary>
    public static OperationModel? FromChain(GeneratorSyntaxContext context, CancellationToken cancellationToken)
    {
        if (context.SemanticModel.GetOperation(context.Node, cancellationToken) is not IInvocationOperation call
            || call.TargetMethod.Name != "WithTisiliaOperation"
            || !HandlerReader.Is(call.TargetMethod.ContainingType, "Tisilia.AspNetCore.TisiliaEndpointExtensions")
            || Argument(call, "operationId")?.Value.ConstantValue is not { HasValue: true, Value: string id })
        {
            // another method of that name, or an id that is not a constant: the exporter reports that it found no declaration
            return null;
        }

        var at = SourceSpot.Of(((MemberAccessExpressionSyntax)((InvocationExpressionSyntax)context.Node).Expression).Name.GetLocation());
        return Guarded(id, at, () => ReadChain(id, at, call, context.SemanticModel.Compilation, cancellationToken));
    }

    private static OperationModel? ReadChain(string id, SourceSpot at, IInvocationOperation call, Compilation compilation, CancellationToken cancellationToken)
    {
        var current = Argument(call, "builder")?.Value;
        while (true)
        {
            current = Unwrap(current);
            if (current is not IInvocationOperation step)
            {
                return HandlerReader.Unreadable(id, at, "the endpoint builder it is called on does not come from a Map call in the same expression");
            }

            var method = step.TargetMethod;
            if (HandlerReader.Is(method.ContainingType, "Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions") && MapMethods.Contains(method.Name))
            {
                var handler = step.Arguments.FirstOrDefault(a => a.Parameter is { Name: "handler" } p && HandlerReader.Is(p.Type, "System.Delegate"));
                return handler is null
                    ? HandlerReader.Unreadable(id, at, "the endpoint is mapped to a RequestDelegate, which returns no result")
                    : FromHandler(id, at, handler.Value, compilation, cancellationToken);
            }

            var receiver = method.IsExtensionMethod ? step.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == 0)?.Value : step.Instance;
            if (receiver is null || !SymbolEqualityComparer.Default.Equals(Unwrap(receiver)?.Type, step.Type))
            {
                return HandlerReader.Unreadable(id, at, $"the endpoint builder it is called on comes from {method.Name}, which is not a Map call");
            }

            current = receiver;
        }
    }

    /// <summary><c>[TisiliaOperation("id")]</c> on the handler itself.</summary>
    public static OperationModel? FromAttribute(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
    {
        if (context.TargetSymbol is not IMethodSymbol method
            || context.Attributes.FirstOrDefault() is not { } attribute
            || attribute.ConstructorArguments.Length != 1
            || attribute.ConstructorArguments[0].Value is not string id)
        {
            return null;
        }

        var at = SourceSpot.Of(attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation() ?? context.TargetNode.GetLocation());
        return Guarded(id, at, () => HandlerReader.Read(id, at, method, KindOf(method), Body(context.SemanticModel.GetOperation(context.TargetNode, cancellationToken)), context.SemanticModel.Compilation));
    }

    /// <summary>
    /// A generator that throws fails the build of a project that treats warnings as errors (CS8785): what this one cannot read
    /// becomes the operation's failure, which export reports, and only cancellation goes through.
    /// </summary>
    internal static OperationModel? Guarded(string id, SourceSpot at, Func<OperationModel?> read)
    {
        try
        {
            return read();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return HandlerReader.Unreadable(id, at, $"Tisilia's source generator could not read it ({e.GetType().Name}: {e.Message})");
        }
    }

    /// <summary>The handler a Map call receives: a lambda, or a method group whose body is in this compilation.</summary>
    private static OperationModel? FromHandler(string id, SourceSpot at, IOperation value, Compilation compilation, CancellationToken cancellationToken)
    {
        var target = Unwrap(value);
        if (target is IDelegateCreationOperation creation)
        {
            target = Unwrap(creation.Target);
        }

        switch (target)
        {
            case IAnonymousFunctionOperation lambda:
                return HandlerReader.Read(id, at, lambda.Symbol, "lambda", lambda.Body, compilation);
            case IMethodReferenceOperation reference:
                var method = reference.Method;
                var syntax = method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken);
                var body = syntax is null ? null : Body(compilation.GetSemanticModel(syntax.SyntaxTree).GetOperation(syntax, cancellationToken));
                return HandlerReader.Read(id, at, method, KindOf(method), body, compilation);
            default:
                return HandlerReader.Unreadable(id, at, "its handler is neither a lambda nor a method group");
        }
    }

    private static IBlockOperation? Body(IOperation? operation) => operation switch
    {
        IMethodBodyOperation method => method.BlockBody ?? method.ExpressionBody,
        ILocalFunctionOperation local => local.Body ?? local.IgnoredBody,
        IAnonymousFunctionOperation lambda => lambda.Body,
        _ => null,
    };

    // a lambda or a local function compiles to a method the exporter cannot find by name: it is matched by where it is written
    private static string KindOf(IMethodSymbol method) =>
        method.MethodKind is MethodKind.AnonymousFunction or MethodKind.LocalFunction ? "lambda" : "method";

    private static IArgumentOperation? Argument(IInvocationOperation call, string name) =>
        call.Arguments.FirstOrDefault(a => a.Parameter?.Name == name);

    private static IOperation? Unwrap(IOperation? operation)
    {
        while (operation is IConversionOperation { Conversion.IsUserDefined: false } conversion)
        {
            operation = conversion.Operand;
        }

        return operation;
    }
}
