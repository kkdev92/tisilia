using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Tisilia.Generator.Canonical;

/// <summary>
/// Whether two builds of the same source can number an assembly's anonymous delegate types differently. Since C# 12 the compiler
/// gives a lambda or method group with an optional or params parameter, converted to <see cref="Delegate"/> (a minimal API route
/// handler, say), a delegate type of its own, <c>&lt;&gt;f__AnonymousDelegate{N}</c>, and numbers these types by the earliest source
/// location it recorded for each. For a generic one, the usual case, it records only the location of the first lambda it binds
/// (AnonymousTypeManager.ConstructAnonymousDelegateImplementationSymbol, through the .NET 10 SDK's compiler), and it binds method
/// bodies in parallel, so which lambda that is depends on the threads. The numbers, and with them the assembly's bytes, can then
/// differ from one build to the next. That takes two numbered types, and a generic one created in more than one place: with one
/// type its number is always 0, and a type created in one place has one location.
/// </summary>
public static class AnonymousDelegates
{
    private const string Prefix = "<>f__AnonymousDelegate";

    private static readonly Dictionary<short, OperandType> Operands = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(opcode => opcode.Value, opcode => opcode.OperandType);

    /// <summary>
    /// The generic anonymous delegate types of <paramref name="assembly"/> that it creates in more than one place, when it has at
    /// least two numbered ones; else empty — also when the file cannot be read as a managed assembly.
    /// </summary>
    public static IReadOnlyList<NumberedDelegate> UnstablyNumbered(string assembly)
    {
        try
        {
            using var stream = File.OpenRead(assembly);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
            {
                return [];
            }

            var reader = pe.GetMetadataReader();
            var numbered = reader.TypeDefinitions
                .Where(handle => IsNumbered(reader, reader.GetTypeDefinition(handle)))
                .ToList();
            var generic = numbered.Where(handle => reader.GetTypeDefinition(handle).GetGenericParameters().Count > 0).ToHashSet();
            if (numbered.Count < 2 || generic.Count == 0)
            {
                return [];
            }

            // a generic one is created through the constructor of an instantiation: a member reference on a type specification
            var constructors = new Dictionary<EntityHandle, TypeDefinitionHandle>();
            foreach (var handle in reader.MemberReferences)
            {
                var member = reader.GetMemberReference(handle);
                if (member.Parent.Kind == HandleKind.TypeSpecification && reader.StringComparer.Equals(member.Name, ".ctor")
                    && Instantiated(reader, (TypeSpecificationHandle)member.Parent) is { } definition && generic.Contains(definition))
                {
                    constructors[handle] = definition;
                }
            }

            var places = new Dictionary<TypeDefinitionHandle, List<string>>();
            foreach (var typeHandle in reader.TypeDefinitions)
            {
                var type = reader.GetTypeDefinition(typeHandle);
                foreach (var methodHandle in type.GetMethods())
                {
                    var method = reader.GetMethodDefinition(methodHandle);
                    if (method.RelativeVirtualAddress == 0)
                    {
                        continue;
                    }

                    foreach (var token in NewObjTokens(pe.GetMethodBody(method.RelativeVirtualAddress).GetILReader()))
                    {
                        if (!constructors.TryGetValue(MetadataTokens.EntityHandle(token), out var definition))
                        {
                            continue;
                        }

                        if (!places.TryGetValue(definition, out var methods))
                        {
                            places[definition] = methods = [];
                        }

                        methods.Add(Owner(reader, type, method));
                    }
                }
            }

            return places
                .Where(entry => entry.Value.Count > 1)
                .Select(entry => new NumberedDelegate(reader.GetString(reader.GetTypeDefinition(entry.Key).Name), entry.Value.Count,
                    entry.Value.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList()))
                .OrderBy(numberedDelegate => numberedDelegate.Name, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException or ArgumentException)
        {
            // not readable as a managed assembly: nothing to report
            return [];
        }
    }

    // top level, in the global namespace, named <>f__AnonymousDelegate{N} (and `{arity} when generic)
    private static bool IsNumbered(MetadataReader reader, TypeDefinition type)
    {
        if (!type.GetDeclaringType().IsNil || !type.Namespace.IsNil)
        {
            return false;
        }

        var name = reader.GetString(type.Name);
        if (!name.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var number = name.AsSpan(Prefix.Length);
        var tick = number.IndexOf('`');
        number = tick < 0 ? number : number[..tick];
        return number.Length > 0 && !number.ContainsAnyExceptInRange('0', '9');
    }

    // the generic type definition of an instantiation signature (GENERICINST CLASS TypeDefOrRef …), else null
    private static TypeDefinitionHandle? Instantiated(MetadataReader reader, TypeSpecificationHandle handle)
    {
        var blob = reader.GetBlobReader(reader.GetTypeSpecification(handle).Signature);
        if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance)
        {
            return null;
        }

        blob.ReadSignatureTypeCode();
        var definition = blob.ReadTypeHandle();
        return definition.Kind == HandleKind.TypeDefinition ? (TypeDefinitionHandle)definition : null;
    }

    // the operand of every newobj in a method body (ECMA-335 III: one or two opcode bytes, then the operand)
    private static List<int> NewObjTokens(BlobReader il)
    {
        var tokens = new List<int>();
        while (il.RemainingBytes > 0)
        {
            var value = (short)il.ReadByte();
            if (value == 0xFE)
            {
                value = (short)(0xFE00 | il.ReadByte());
            }

            if (value == OpCodes.Newobj.Value)
            {
                tokens.Add(il.ReadInt32());
                continue;
            }

            var size = Operands.TryGetValue(value, out var operand) ? operand switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 * il.ReadInt32(), // after the count, one target per case
                _ => 4,
            } : 0;
            il.Offset += size;
        }

        return tokens;
    }

    // the method of the user's type that holds the lambda or method group: lambda and local function bodies (<Map>b__0_1,
    // <Map>g__Local|0_0) and async state machines (<Map>d__3) are named after it, closure classes (<>c…) nest in the type
    private static string Owner(MetadataReader reader, TypeDefinition type, MethodDefinition method)
    {
        var name = Generated(reader.GetString(method.Name)) ?? Generated(reader.GetString(type.Name)) ?? reader.GetString(method.Name);
        while (reader.GetString(type.Name).StartsWith('<') && !type.GetDeclaringType().IsNil)
        {
            type = reader.GetTypeDefinition(type.GetDeclaringType());
        }

        return $"{reader.GetString(type.Name)}.{name}";

        // the name between the leading < and its matching > (<<Main>$>b__0_0 holds <Main>$), else null (<>c names nothing)
        static string? Generated(string name)
        {
            var depth = 0;
            for (var i = 0; i < name.Length && name[0] == '<'; i++)
            {
                depth += name[i] switch { '<' => 1, '>' => -1, _ => 0 };
                if (depth == 0)
                {
                    return i > 1 ? Generated(name[1..i]) ?? name[1..i] : null;
                }
            }

            return null;
        }
    }
}

/// <summary>A generic anonymous delegate type, how many times the assembly creates it, and the methods that do.</summary>
public sealed record NumberedDelegate(string Name, int Uses, IReadOnlyList<string> Methods);
