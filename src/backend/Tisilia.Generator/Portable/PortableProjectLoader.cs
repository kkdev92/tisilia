using System.Text.Json;
using Tisilia.Contract;
using Tisilia.Documents;
using Tisilia.Generator.Canonical;
using Tisilia.Generator.Diagnostics;
using Tisilia.Generator.Validation;

namespace Tisilia.Generator.Portable;

/// <summary>One definition with its resolved programs.</summary>
public sealed record LoadedDefinition(PortableDefinition Definition, string File, string ProjectId)
{
    /// <summary>The program the TypeScript request encoder writes: the response program when the request read has token unions, else the request program.</summary>
    public Program? CanonicalWriter { get; init; }

    /// <summary>
    /// True for a definition derived by the loader for an object/union model that programs describe inline (a nested object, a
    /// union variant or a nested union): such a model is a codec of the module like any definition, implemented by the enclosing
    /// programs' object/tagged-union op (one program per direction).
    /// </summary>
    public bool Synthesized { get; init; }
}

/// <summary>A project with its transitive imports resolved.</summary>
public sealed class LoadedPortableProject
{
    public required PortableProject Project { get; init; }
    public required string File { get; init; }
    public required string Directory { get; init; }
    public required IReadOnlyList<LoadedDefinition> Definitions { get; init; }
    public required IReadOnlyDictionary<string, LoadedDefinition> DefinitionsById { get; init; }
    public required IReadOnlyDictionary<string, Model> Models { get; init; }
    public required IReadOnlyList<LoadedPortableProject> Imports { get; init; }
    public required string CsharpOutput { get; init; }
    public required string TypescriptOutput { get; init; }

    /// <summary>Union model id → discriminator member name, recorded from the tagged-union programs of the closure.</summary>
    public IReadOnlyDictionary<string, string> Discriminators { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Builtin structural equivalences of the synthesized definitions (declared for the module manifest like the project's own).</summary>
    public IReadOnlyList<Equivalence> SynthesizedEquivalences { get; init; } = [];
}

/// <summary>
/// Loads a <c>tisilia.portable-project</c> with its definitions and imports and applies SV40–SV42: import digests and
/// project-level cycles, id uniqueness across the import closure, builtin scalar/representation correspondence,
/// program/model correspondence, token unions only on the read side with all branches reaching the same domain
/// and a unique writer, productive recursion through refs.
/// </summary>
public static class PortableProjectLoader
{
    public static LoadedPortableProject? Load(string projectPath, DiagnosticBag bag) => Load(projectPath, bag, [], new Dictionary<string, LoadedPortableProject>(StringComparer.Ordinal));

    private static LoadedPortableProject? Load(string projectPath, DiagnosticBag bag, List<string> stack, Dictionary<string, LoadedPortableProject> loaded)
    {
        var full = Path.GetFullPath(projectPath);
        if (loaded.TryGetValue(full, out var already))
        {
            return already;
        }

        if (stack.Contains(full, StringComparer.OrdinalIgnoreCase))
        {
            bag.Error(TisiliaCodes.PortableProject, "SV40", "/imports", $"portable project import cycle: {string.Join(" -> ", stack.Append(full))}");
            return null;
        }

        if (!File.Exists(full))
        {
            bag.Error(TisiliaCodes.PortableProject, "SV40", "", $"portable project not found: {full}");
            return null;
        }

        var node = TisiliaSchemas.ParseStrict(File.ReadAllText(full), bag);
        if (node is null || !TisiliaSchemas.Instance.ValidateStructure(DocumentKind.PortableProject, node, bag))
        {
            return null;
        }

        var project = JsonSerializer.Deserialize<PortableProject>(node, TisiliaJson.Options)!;
        if (project.BuiltinSet != TisiliaJson.BuiltinSet)
        {
            bag.Error(TisiliaCodes.PortableProject, "SV40", "/builtinSet", $"portable project '{project.ProjectId}' names builtin set '{project.BuiltinSet}'; only {TisiliaJson.BuiltinSet} exists");
            return null;
        }

        var dir = Path.GetDirectoryName(full)!;
        stack.Add(full);
        var imports = new List<LoadedPortableProject>();
        var index = 0;
        foreach (var import in project.Imports)
        {
            var importPath = Path.GetFullPath(Path.Combine(dir, import.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(importPath))
            {
                bag.Error(TisiliaCodes.PortableProject, "SV40", $"/imports/{index}", $"import '{import.Path}' not found at {importPath}");
                index++;
                continue;
            }

            // an import is a JSON document: a CRLF checkout of it is the same import (LineEndings)
            var bytes = File.ReadAllBytes(importPath);
            if (!LineEndings.StillWritten(bytes, import.Digest))
            {
                bag.Error(TisiliaCodes.PortableProject, "SV40", $"/imports/{index}/digest", $"import '{import.Path}' has digest {TisiliaHash.Sha256OfBytes(bytes)}, manifest records {import.Digest}" + (LineEndings.ArtifactHint(bytes, import.Digest, importPath) is { } hint ? "; " + hint : ""));
                index++;
                continue;
            }

            var imported = Load(importPath, bag, stack, loaded);
            if (imported is not null)
            {
                imports.Add(imported);
            }

            index++;
        }

        stack.RemoveAt(stack.Count - 1);

        var definitions = new List<LoadedDefinition>();
        index = 0;
        foreach (var relative in project.Definitions)
        {
            var defPath = Path.GetFullPath(Path.Combine(dir, relative.Replace('/', Path.DirectorySeparatorChar)));
            var defBag = new DiagnosticBag { File = defPath };
            if (!File.Exists(defPath))
            {
                bag.Error(TisiliaCodes.PortableProject, "SV40", $"/definitions/{index}", $"definition '{relative}' not found at {defPath}");
                index++;
                continue;
            }

            var defNode = TisiliaSchemas.ParseStrict(File.ReadAllText(defPath), defBag);
            if (defNode is null || !TisiliaSchemas.Instance.ValidateStructure(DocumentKind.PortableDefinition, defNode, defBag))
            {
                bag.AddRange(defBag.Items);
                index++;
                continue;
            }

            PortableDefinition definition;
            try
            {
                definition = JsonSerializer.Deserialize<PortableDefinition>(defNode, TisiliaJson.Options)!;
            }
            catch (JsonException e)
            {
                bag.Error(TisiliaCodes.PortableProject, "SV40", $"/definitions/{index}", $"definition '{relative}': {e.Message}");
                index++;
                continue;
            }

            definitions.Add(new LoadedDefinition(definition, defPath, project.ProjectId));
            index++;
        }

        // id uniqueness across the import closure (SV40)
        var byId = new Dictionary<string, LoadedDefinition>(StringComparer.Ordinal);
        var models = new Dictionary<string, Model>(StringComparer.Ordinal);
        foreach (var imported in imports)
        {
            foreach (var (id, def) in imported.DefinitionsById)
            {
                if (!byId.TryAdd(id, def))
                {
                    bag.Error(TisiliaCodes.PortableProject, "SV40", "/imports", $"definition id '{id}' is provided by more than one imported project", [id]);
                }
            }

            foreach (var (id, model) in imported.Models)
            {
                models.TryAdd(id, model);
            }
        }

        foreach (var def in definitions)
        {
            if (!byId.TryAdd(def.Definition.Id, def))
            {
                bag.Error(TisiliaCodes.PortableProject, "SV40", "/definitions", $"definition id '{def.Definition.Id}' is declared more than once in the import closure", [def.Definition.Id]);
            }
        }

        foreach (var model in project.Models)
        {
            if (!models.TryAdd(model.Id, model))
            {
                bag.Error(TisiliaCodes.PortableProject, "SV40", "/models", $"model id '{model.Id}' is declared more than once in the import closure", [model.Id]);
            }
        }

        var result = new LoadedPortableProject
        {
            Project = project,
            File = full,
            Directory = dir,
            Definitions = definitions,
            DefinitionsById = byId,
            Models = models,
            Imports = imports,
            CsharpOutput = Path.GetFullPath(Path.Combine(dir, project.Output.Csharp.Replace('/', Path.DirectorySeparatorChar))),
            TypescriptOutput = Path.GetFullPath(Path.Combine(dir, project.Output.Typescript.Replace('/', Path.DirectorySeparatorChar))),
        };
        var discriminators = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var import in imports)
        {
            foreach (var (unionId, discriminator) in import.Discriminators)
            {
                discriminators[unionId] = discriminator;
            }
        }

        var validated = definitions.Select(d => Validate(d, result, bag, discriminators)).ToList();
        var final = new LoadedPortableProject
        {
            Project = project,
            File = full,
            Directory = dir,
            Definitions = validated,
            DefinitionsById = ById(validated.Concat(imports.SelectMany(i => i.Definitions))),
            Models = models,
            Imports = imports,
            CsharpOutput = result.CsharpOutput,
            TypescriptOutput = result.TypescriptOutput,
            Discriminators = discriminators,
        };
        CheckRecursion(final, bag);
        var complete = Synthesize(final, bag);
        loaded[full] = complete;
        return complete;
    }

    // ------------------------------------------------------------------ SV41 / SV42

    private static LoadedDefinition Validate(LoadedDefinition loaded, LoadedPortableProject project, DiagnosticBag bag, Dictionary<string, string> discriminators)
    {
        var def = loaded.Definition;
        var path = "/" + def.Id;
        Program? writer = null;
        if (def.Response is { } response)
        {
            CheckProgram(response.Program, response.DomainTypeId, project, bag, path + "/response/program", allowTokenUnion: false, def.Id, discriminators);
            CheckDirectionRefs(response, project, bag, path + "/response");
        }

        if (def.Request is { } request)
        {
            CheckProgram(request.Program, request.DomainTypeId, project, bag, path + "/request/program", allowTokenUnion: true, def.Id, discriminators);
            CheckDirectionRefs(request, project, bag, path + "/request");
            if (ContainsTokenUnion(request.Program))
            {
                if (def.Response is null)
                {
                    bag.Error(TisiliaCodes.PortableTokenUnion, "SV41", path + "/request/program", $"definition '{def.Id}' reads a token union but has no response program: the request encoder needs the response side's unique writer", [def.Id]);
                }
                else if (def.Response.DomainTypeId != request.DomainTypeId || def.Response.ProjectionId != request.ProjectionId)
                {
                    bag.Error(TisiliaCodes.PortableTokenUnion, "SV41", path + "/request/program", $"definition '{def.Id}' reads a token union, so its response program must have the same domain type and projection as the request", [def.Id]);
                }
                else
                {
                    writer = def.Response.Program;
                }
            }
            else
            {
                writer = request.Program;
            }
        }

        if (def.Request is null && def.Response is null)
        {
            bag.Error(TisiliaCodes.PortableProject, "SV40", path, $"definition '{def.Id}' has neither a request nor a response program", [def.Id]);
        }

        return loaded with { CanonicalWriter = writer };
    }

    private static void CheckDirectionRefs(PortableDirection direction, LoadedPortableProject project, DiagnosticBag bag, string path)
    {
        if (!project.Models.ContainsKey(direction.DomainTypeId) && !ContractBuilderStdType(direction.DomainTypeId))
        {
            bag.Error(TisiliaCodes.UnresolvedReference, "SV03", path + "/domainTypeId", $"domain type '{direction.DomainTypeId}' is not a model of the project or its imports", [direction.DomainTypeId]);
        }

        if (direction.ProjectionId != Builtins.ProjectionIdentity && !project.Project.Projections.Any(p => p.Id == direction.ProjectionId) && !project.Imports.Any(i => i.Project.Projections.Any(p => p.Id == direction.ProjectionId)))
        {
            bag.Error(TisiliaCodes.UnresolvedReference, "SV22", path + "/projectionId", $"projection '{direction.ProjectionId}' is neither the identity builtin nor declared in the project", [direction.ProjectionId]);
        }

        if (!project.Project.Equivalences.Any(e => e.Id == direction.EquivalenceId) && !project.Imports.Any(i => i.Project.Equivalences.Any(e => e.Id == direction.EquivalenceId)))
        {
            bag.Error(TisiliaCodes.UnresolvedReference, "SV21", path + "/equivalenceId", $"equivalence '{direction.EquivalenceId}' is not declared in the project", [direction.EquivalenceId]);
        }
    }

    private static bool ContractBuilderStdType(string typeId) => typeId.StartsWith("std.", StringComparison.Ordinal) && Builtins.ScalarNames.Contains(typeId["std.".Length..]);

    public static bool ContainsTokenUnion(Program program) => program switch
    {
        TokenUnionOp => true,
        NullableOp n => ContainsTokenUnion(n.Value),
        ArrayOp a => ContainsTokenUnion(a.Element),
        ObjectOp o => o.Members.Any(m => ContainsTokenUnion(m.Node)),
        TaggedUnionOp t => t.Branches.Any(b => ContainsTokenUnion(b.Node)),
        _ => false,
    };

    /// <summary>
    /// The domain type a program produces, checked against the declared model shape at every level. <paramref name="discriminator"/>
    /// is set for the object program of a tagged-union branch: that member is implicit (written by the union) and must not be declared.
    /// </summary>
    private static void CheckProgram(Program program, string domainTypeId, LoadedPortableProject project, DiagnosticBag bag, string path, bool allowTokenUnion, string definitionId, Dictionary<string, string> discriminators, string? discriminator = null)
    {
        var scalarOfModel = project.Models.TryGetValue(domainTypeId, out var m) && m.Shape is PrimitiveShape p ? p.PrimitiveId : ContractBuilderStdType(domainTypeId) ? Builtins.Scalar(domainTypeId["std.".Length..]) : null;
        switch (program)
        {
            case ScalarOp scalar:
                if (!Builtins.Is(scalar.ScalarId, BuiltinKind.Scalar))
                {
                    bag.Error(TisiliaCodes.PortableResponseUnion, "SV42", path + "/scalarId", $"'{scalar.ScalarId}' is not a builtin scalar of {TisiliaJson.BuiltinSet}", [definitionId]);
                    return;
                }

                if (scalarOfModel is not null && scalarOfModel != scalar.ScalarId)
                {
                    bag.Error(TisiliaCodes.PortableResponseUnion, "SV42", path, $"scalar op '{scalar.ScalarId}' does not match domain type '{domainTypeId}'", [definitionId]);
                }

                if (scalarOfModel is null)
                {
                    bag.Error(TisiliaCodes.PortableResponseUnion, "SV42", path, $"scalar op at a position whose domain '{domainTypeId}' is not a primitive", [definitionId]);
                }

                var name = scalar.ScalarId["tisilia.".Length..^"@0.1".Length];
                if (scalar.Representation == ScalarRepresentation.Native && !Builtins.IsNumberTokenScalar(name) && name != "boolean" && name != "string" && name != "json-value")
                {
                    // dates, guid, bytes, char are strings on the wire: "native" means their System.Text.Json string form
                }

                if (scalar.Representation == ScalarRepresentation.String && (name is "string" or "boolean" or "json-value" || !Builtins.IsNumberTokenScalar(name)))
                {
                    bag.Error(TisiliaCodes.PortableResponseUnion, "SV42", path + "/representation", $"'string' representation applies to number scalars only; '{name}' is already a string on the wire", [definitionId]);
                }

                return;
            case RefOp reference:
            {
                if (!project.DefinitionsById.TryGetValue(reference.DefinitionId, out var target) && !project.Definitions.Any(d => d.Definition.Id == reference.DefinitionId))
                {
                    bag.Error(TisiliaCodes.UnresolvedReference, "SV40", path + "/definitionId", $"ref to unknown definition '{reference.DefinitionId}'", [definitionId, reference.DefinitionId]);
                    return;
                }

                target ??= project.Definitions.First(d => d.Definition.Id == reference.DefinitionId);
                var targetDomain = (target.Definition.Request ?? target.Definition.Response)?.DomainTypeId;
                if (targetDomain != domainTypeId)
                {
                    bag.Error(TisiliaCodes.PortableResponseUnion, "SV42", path, $"ref '{reference.DefinitionId}' produces domain '{targetDomain}' but this position expects '{domainTypeId}'", [definitionId]);
                }

                return;
            }

            case NullableOp nullable:
                CheckProgram(nullable.Value, domainTypeId, project, bag, path + "/value", allowTokenUnion, definitionId, discriminators);
                return;
            case ArrayOp array:
            {
                if (!project.Models.TryGetValue(domainTypeId, out var model) || model.Shape is not ArrayShape shape)
                {
                    bag.Error(TisiliaCodes.PortableResponseUnion, "SV42", path, $"array op requires an array model at '{domainTypeId}'", [definitionId]);
                    return;
                }

                CheckProgram(array.Element, shape.Element.TypeId, project, bag, path + "/element", allowTokenUnion, definitionId, discriminators);
                return;
            }

            case ObjectOp obj:
            {
                if (!project.Models.TryGetValue(domainTypeId, out var model) || model.Shape is not ObjectShape shape)
                {
                    bag.Error(TisiliaCodes.PortableResponseUnion, "SV42", path, $"object op requires an object model at '{domainTypeId}'", [definitionId]);
                    return;
                }

                var seen = new HashSet<string>(StringComparer.Ordinal);
                var i = 0;
                foreach (var member in obj.Members)
                {
                    var mp = path + "/members/" + i++;
                    if (!seen.Add(member.Name))
                    {
                        bag.Error(TisiliaCodes.PortableResponseUnion, "SV42", mp, $"object member '{member.Name}' is declared twice", [definitionId]);
                        continue;
                    }

                    if (member.Name == discriminator)
                    {
                        bag.Error(TisiliaCodes.PortableResponseUnion, "SV42", mp, $"member '{member.Name}' is the discriminator of the enclosing tagged union; it is written by the union and must not be declared in the branch", [definitionId]);
                        continue;
                    }

                    var prop = shape.Properties.FirstOrDefault(pr => pr.Name == member.Name);
                    if (prop is null)
                    {
                        bag.Error(TisiliaCodes.PortableResponseUnion, "SV42", mp, $"object member '{member.Name}' has no property in model '{domainTypeId}'", [definitionId]);
                        continue;
                    }

                    if (member.Node is NullableOp != prop.Use.SemanticNullable && member.Node is not NullableOp && prop.Use.SemanticNullable)
                    {
                        bag.Error(TisiliaCodes.PortableResponseUnion, "SV42", mp, $"member '{member.Name}' is nullable in the model but the program has no nullable op", [definitionId]);
                    }

                    CheckProgram(member.Node, prop.Use.TypeId, project, bag, mp + "/node", allowTokenUnion, definitionId, discriminators);
                }

                foreach (var prop in shape.Properties)
                {
                    if (!seen.Contains(prop.Name) && prop.Presence == Presence.Required && prop.Name != discriminator)
                    {
                        bag.Error(TisiliaCodes.PortableResponseUnion, "SV42", path, $"required model property '{prop.Name}' has no object member in the program", [definitionId]);
                    }
                }

                return;
            }

            case TokenUnionOp union:
            {
                if (!allowTokenUnion)
                {
                    bag.Error(TisiliaCodes.PortableResponseUnion, "SV42", path, "token unions are not allowed in response programs", [definitionId]);
                    return;
                }

                var tokens = new HashSet<JsonToken>();
                var i = 0;
                foreach (var branch in union.Branches)
                {
                    var bp = path + "/branches/" + i++;
                    if (!tokens.Add(branch.Token))
                    {
                        bag.Error(TisiliaCodes.PortableTokenUnion, "SV41", bp, $"token '{branch.Token}' has more than one read branch; branches must not overlap", [definitionId]);
                    }

                    if (branch.Node is TokenUnionOp)
                    {
                        bag.Error(TisiliaCodes.PortableTokenUnion, "SV41", bp, "nested token unions are not allowed", [definitionId]);
                    }

                    if (RootToken(branch.Node) is { } root && root != branch.Token)
                    {
                        bag.Error(TisiliaCodes.PortableTokenUnion, "SV41", bp, $"branch for token '{branch.Token}' reads a '{root}' node", [definitionId]);
                    }

                    // every branch reaches the same domain (the position's domain type)
                    CheckProgram(branch.Node, domainTypeId, project, bag, bp + "/node", allowTokenUnion: false, definitionId, discriminators);
                }

                return;
            }

            case TaggedUnionOp tagged:
            {
                if (!project.Models.TryGetValue(domainTypeId, out var model) || model.Shape is not UnionShape shape)
                {
                    bag.Error(TisiliaCodes.PortableResponseUnion, "SV42", path, $"tagged-union op requires a union model at '{domainTypeId}'", [definitionId]);
                    return;
                }

                if (discriminators.TryGetValue(domainTypeId, out var recorded) && recorded != tagged.Discriminator)
                {
                    bag.Error(TisiliaCodes.PortableResponseUnion, "SV42", path + "/discriminator", $"union model '{domainTypeId}' is read/written with discriminator '{recorded}' elsewhere; one union has one discriminator member", [definitionId]);
                }
                else
                {
                    discriminators[domainTypeId] = tagged.Discriminator;
                }

                var tags = new HashSet<string>(StringComparer.Ordinal);
                var i = 0;
                foreach (var branch in tagged.Branches)
                {
                    var bp = path + "/branches/" + i++;
                    if (!tags.Add(branch.Tag))
                    {
                        bag.Error(TisiliaCodes.TaggedUnionInvalid, "SV13", bp, $"tag '{branch.Tag}' is used by more than one branch", [definitionId]);
                        continue;
                    }

                    var variant = shape.Variants.FirstOrDefault(v => v.Tag == branch.Tag);
                    if (variant is null)
                    {
                        bag.Error(TisiliaCodes.PortableResponseUnion, "SV42", bp, $"tag '{branch.Tag}' has no variant in union model '{domainTypeId}'", [definitionId]);
                        continue;
                    }

                    if (branch.Node is not ObjectOp)
                    {
                        bag.Error(TisiliaCodes.PortableResponseUnion, "SV42", bp, "tagged-union branches must be object programs", [definitionId]);
                        continue;
                    }

                    // the variant is an object model whose first property is the discriminator (a required string), as the contract
                    // represents polymorphic objects; the branch program reads/writes the other members
                    if (project.Models.GetValueOrDefault(variant.Use.TypeId)?.Shape is not ObjectShape variantShape)
                    {
                        bag.Error(TisiliaCodes.PortableResponseUnion, "SV42", bp, $"variant '{branch.Tag}' of union model '{domainTypeId}' must be an object model", [definitionId]);
                        continue;
                    }

                    var first = variantShape.Properties.Count > 0 ? variantShape.Properties[0] : null;
                    if (first is null || first.Name != tagged.Discriminator || first.Use.TypeId != "std.string" || first.Use.SemanticNullable || first.Presence != Presence.Required)
                    {
                        bag.Error(TisiliaCodes.PortableResponseUnion, "SV42", bp, $"variant model '{variant.Use.TypeId}' must declare the discriminator '{tagged.Discriminator}' as its first property (required std.string)", [definitionId]);
                    }

                    CheckProgram(branch.Node, variant.Use.TypeId, project, bag, bp + "/node", allowTokenUnion, definitionId, discriminators, tagged.Discriminator);
                }

                foreach (var variant in shape.Variants)
                {
                    if (!tags.Contains(variant.Tag))
                    {
                        bag.Error(TisiliaCodes.PortableResponseUnion, "SV42", path, $"variant '{variant.Tag}' of union model '{domainTypeId}' has no branch in the program; every variant must be readable/writable", [definitionId]);
                    }
                }

                return;
            }
        }
    }

    /// <summary>Definitions by id; a duplicate id was already reported as SV40, the first declaration wins so loading can continue.</summary>
    private static Dictionary<string, LoadedDefinition> ById(IEnumerable<LoadedDefinition> definitions)
    {
        var byId = new Dictionary<string, LoadedDefinition>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            byId.TryAdd(definition.Definition.Id, definition);
        }

        return byId;
    }

    /// <summary>
    /// Every object/union model that programs describe inline becomes a definition of its own (one program per
    /// direction per model), so the contract carries a truthful module-implemented codec for it instead of a structural one.
    /// </summary>
    private static LoadedPortableProject Synthesize(LoadedPortableProject project, DiagnosticBag bag)
    {
        var extra = new List<LoadedDefinition>();
        var equivalences = new List<Equivalence>();
        foreach (var model in project.Project.Models.OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            if (model.Shape is not (ObjectShape or UnionShape) || PortableModel.DefinitionOfModel(project, model.Id) is not null)
            {
                continue;
            }

            var (request, requestOwner) = ProgramOf(project, model.Id, request: true, bag);
            var (response, responseOwner) = ProgramOf(project, model.Id, request: false, bag);
            if (request is null && response is null)
            {
                continue; // not reached by any program: no codec is needed or possible
            }

            var owner = requestOwner ?? responseOwner!;
            var structure = model.Shape is UnionShape ? "union" : "object";
            foreach (var (scope, suffix) in new[] { (EquivalenceScope.Request, "request"), (EquivalenceScope.Response, "response") })
            {
                equivalences.Add(new Equivalence
                {
                    Id = model.Id + "." + suffix,
                    Version = owner.Definition.DefinitionVersion,
                    DomainTypeId = model.Id,
                    Scope = scope,
                    Grade = Grade.G2,
                    DomainRuleId = Builtins.DomainRule(structure),
                    DotnetOracle = new BuiltinImpl { Id = Builtins.OracleStructural },
                    TypescriptOracle = new BuiltinImpl { Id = Builtins.OracleStructural },
                    NormalizationId = Builtins.NormalizeIdentity,
                    Preserved = ["structure", "member-values"],
                    NotPreserved = [],
                });
            }

            var definition = new PortableDefinition
            {
                Format = "tisilia.portable-definition",
                Version = TisiliaJson.DraftVersion,
                Id = model.Id,
                DefinitionVersion = owner.Definition.DefinitionVersion,
                ClrType = model.ClrIdentity,
                Request = request is null ? null : new PortableDirection { DomainTypeId = model.Id, Program = request, ProjectionId = Builtins.ProjectionIdentity, EquivalenceId = model.Id + ".request" },
                Response = response is null ? null : new PortableDirection { DomainTypeId = model.Id, Program = response, ProjectionId = Builtins.ProjectionIdentity, EquivalenceId = model.Id + ".response" },
            };
            Program? writer = request;
            if (request is not null && ContainsTokenUnion(request))
            {
                if (response is null)
                {
                    bag.Error(TisiliaCodes.PortableTokenUnion, "SV41", "/models/" + model.Id, $"model '{model.Id}' is read with a token union but never written: the request encoder needs a response program's unique writer", [model.Id]);
                }

                writer = response;
            }

            extra.Add(new LoadedDefinition(definition, owner.File, project.Project.ProjectId) { CanonicalWriter = writer, Synthesized = true });
        }

        if (extra.Count == 0)
        {
            return project;
        }

        var definitions = project.Definitions.Concat(extra).ToList();
        return new LoadedPortableProject
        {
            Project = project.Project,
            File = project.File,
            Directory = project.Directory,
            Definitions = definitions,
            DefinitionsById = ById(definitions.Concat(project.Imports.SelectMany(i => i.Definitions))),
            Models = project.Models,
            Imports = project.Imports,
            CsharpOutput = project.CsharpOutput,
            TypescriptOutput = project.TypescriptOutput,
            Discriminators = project.Discriminators,
            SynthesizedEquivalences = equivalences,
        };
    }

    /// <summary>The single object/tagged-union program that produces a model in a direction, with the definition it was found in; differing programs are rejected.</summary>
    private static (Program? Program, LoadedDefinition? Owner) ProgramOf(LoadedPortableProject project, string modelId, bool request, DiagnosticBag bag)
    {
        var found = new List<(Program Program, LoadedDefinition Owner)>();
        foreach (var def in project.Definitions.OrderBy(d => d.Definition.Id, StringComparer.Ordinal))
        {
            var direction = request ? def.Definition.Request : def.Definition.Response;
            if (direction is not null)
            {
                CollectPrograms(direction.Program, direction.DomainTypeId, modelId, def, project, found);
            }
        }

        if (found.Count == 0)
        {
            return (null, null);
        }

        var canonical = JsonSerializer.Serialize(found[0].Program, TisiliaJson.Options);
        foreach (var (program, owner) in found.Skip(1))
        {
            if (JsonSerializer.Serialize(program, TisiliaJson.Options) != canonical)
            {
                bag.Error(TisiliaCodes.PortableResponseUnion, "SV42", "/" + owner.Definition.Id, $"model '{modelId}' is {(request ? "read" : "written")} by different programs in '{found[0].Owner.Definition.Id}' and '{owner.Definition.Id}'; one model has one wire per direction (declare a second model for the other shape)", [modelId]);
                break;
            }
        }

        return (found[0].Program, found[0].Owner);
    }

    private static void CollectPrograms(Program node, string domainTypeId, string modelId, LoadedDefinition owner, LoadedPortableProject project, List<(Program Program, LoadedDefinition Owner)> found)
    {
        switch (node)
        {
            case ObjectOp obj:
            {
                if (domainTypeId == modelId)
                {
                    found.Add((node, owner));
                }

                if (project.Models.GetValueOrDefault(domainTypeId)?.Shape is ObjectShape shape)
                {
                    foreach (var member in obj.Members)
                    {
                        if (shape.Properties.FirstOrDefault(p => p.Name == member.Name) is { } prop)
                        {
                            CollectPrograms(member.Node, prop.Use.TypeId, modelId, owner, project, found);
                        }
                    }
                }

                return;
            }

            case TaggedUnionOp tagged:
            {
                if (domainTypeId == modelId)
                {
                    found.Add((node, owner));
                }

                if (project.Models.GetValueOrDefault(domainTypeId)?.Shape is UnionShape union)
                {
                    foreach (var branch in tagged.Branches)
                    {
                        if (union.Variants.FirstOrDefault(v => v.Tag == branch.Tag) is { } variant)
                        {
                            CollectPrograms(branch.Node, variant.Use.TypeId, modelId, owner, project, found);
                        }
                    }
                }

                return;
            }

            case NullableOp nullable:
                CollectPrograms(nullable.Value, domainTypeId, modelId, owner, project, found);
                return;
            case ArrayOp array:
                if (project.Models.GetValueOrDefault(domainTypeId)?.Shape is ArrayShape arrayShape)
                {
                    CollectPrograms(array.Element, arrayShape.Element.TypeId, modelId, owner, project, found);
                }

                return;
            case TokenUnionOp union:
                foreach (var branch in union.Branches)
                {
                    CollectPrograms(branch.Node, domainTypeId, modelId, owner, project, found);
                }

                return;
            default:
                return;
        }
    }

    /// <summary>Recursion through refs must be productive, i.e. every cycle passes a nullable, an array element or an optional member.</summary>
    private static void CheckRecursion(LoadedPortableProject project, DiagnosticBag bag)
    {
        foreach (var def in project.Definitions)
        {
            foreach (var (direction, name) in new[] { (def.Definition.Request, "request"), (def.Definition.Response, "response") })
            {
                if (direction is not null && ReachesUnguarded(direction.Program, def.Definition.Id, name, project, new HashSet<string>(StringComparer.Ordinal)))
                {
                    bag.Error(TisiliaCodes.PortableProject, "SV40", "/" + def.Definition.Id + "/" + name + "/program", $"definition '{def.Definition.Id}' recurses through refs without a nullable, array or optional member on the cycle (non-productive recursion)", [def.Definition.Id]);
                }
            }
        }
    }

    private static bool ReachesUnguarded(Program program, string targetId, string direction, LoadedPortableProject project, HashSet<string> visited)
    {
        switch (program)
        {
            case RefOp reference:
            {
                if (reference.DefinitionId == targetId)
                {
                    return true;
                }

                if (!visited.Add(reference.DefinitionId) || !project.DefinitionsById.TryGetValue(reference.DefinitionId, out var target))
                {
                    return false;
                }

                var next = direction == "request" ? target.Definition.Request?.Program : target.Definition.Response?.Program;
                return next is not null && ReachesUnguarded(next, targetId, direction, project, visited);
            }

            case ObjectOp obj:
                return obj.Members.Where(m => m.Presence == Presence.Required).Any(m => ReachesUnguarded(m.Node, targetId, direction, project, visited));
            case TokenUnionOp union:
                return union.Branches.Any(b => ReachesUnguarded(b.Node, targetId, direction, project, visited));
            case TaggedUnionOp tagged:
                return tagged.Branches.Any(b => ReachesUnguarded(b.Node, targetId, direction, project, visited));
            default:
                return false; // scalars terminate; nullable and array positions guard the recursion
        }
    }

    private static JsonToken? RootToken(Program program) => program switch
    {
        ScalarOp s => s.Representation == ScalarRepresentation.String ? JsonToken.String : s.ScalarId["tisilia.".Length..^"@0.1".Length] switch
        {
            "boolean" => JsonToken.Boolean,
            "json-value" => null,
            var n when Builtins.IsNumberTokenScalar(n) => JsonToken.Number,
            _ => JsonToken.String,
        },
        ObjectOp or TaggedUnionOp => JsonToken.Object,
        ArrayOp => JsonToken.Array,
        NullableOp => null,
        _ => null,
    };
}
