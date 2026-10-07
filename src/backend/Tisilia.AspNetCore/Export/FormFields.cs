using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Tisilia.Contract;
using Tisilia.Generator.Building;
using Tisilia.Generator.Diagnostics;

namespace Tisilia.AspNetCore.Export;

public sealed partial class TisiliaContractExporter
{
    private static void AddFormFields(ApiParameterDescription parameter, bool mvc, bool apiController, Func<ClrTypeMapper> mapper,
        ContractBuilder builder, DiagnosticBag bag, string operationId, string path, List<FormField> fields)
    {
        var info = (parameter.ParameterDescriptor as IParameterInfoParameterDescriptor)?.ParameterInfo;
        var required = parameter.IsRequired || (apiController && parameter.ModelMetadata?.IsRequired == true);
        var (element, repeated) = UnwrapCollection(parameter.Type);
        if (parameter.Type == typeof(IFormFileCollection)) { element = typeof(IFormFile); repeated = true; }
        var count = 0;
        if (parameter.Type.IsArray && parameter.Type.GetArrayRank() != 1) { Unsupported("multidimensional form arrays need an explicit binding"); return; }
        if (!mvc && repeated && element == typeof(IFormFile) && parameter.Type != typeof(IFormFileCollection))
        {
            Unsupported("root file collections require IFormFileCollection; named collections belong on a model"); return;
        }
        var rootIndexed = !mvc && repeated && parameter.Type != typeof(IFormFileCollection) && !parameter.Type.IsArray;
        if (TryLeaf(element, parameter.Name, repeated, indexed: rootIndexed, required, fields,
            mvc && parameter.ModelMetadata?.ConvertEmptyStringToNull != false, info?.HasDefaultValue == true, info?.DefaultValue,
            rootIndexed ? "" : null)) { return; }
        if (mvc) { Unsupported("complex MVC forms need an explicit binding"); return; }
        // FormDataMapper reads complex parameters at the form root, including root collections.
        if (repeated) { AddObject(element, parameter.Name, true, required, fields, [], 0, ""); }
        else { Walk(parameter.Type, "", fields, [], 0); }

        bool TryLeaf(Type type, string name, bool many, bool indexed, bool needed, List<FormField> target, bool rejectBlank = false, bool hasDefault = false, object? defaultValue = null, string? wireName = null)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            var file = type == typeof(IFormFile);
            var scalar = type == typeof(DateTime) ? "datetime" : ClrTypeMapper.ScalarNameOf(type);
            if (!file && !type.IsEnum && (scalar is null or "bytes" or "json-value" or "datetime-local-wire")) { return false; }
            if (mvc && !file && !type.IsEnum && scalar != "string") { Unsupported("MVC numeric/date form values depend on request culture"); return true; }
            var use = file ? null : type.IsEnum ? mapper().Map(type, WireDirection.ServerRead, path, isRoot: false) : builder.Scalar(scalar!, NumberProfile.Strict);
            if (!file && use is null) { return true; }
            if (hasDefault && defaultValue is Enum) { defaultValue = Convert.ChangeType(defaultValue, Enum.GetUnderlyingType(type), System.Globalization.CultureInfo.InvariantCulture); }
            count++;
            target.Add(new FormField
            {
                Name = name,
                Kind = file ? "file" : "value",
                Use = use,
                Repeated = many,
                Indexed = indexed && many,
                WireName = wireName,
                EnumDefinedOnly = mvc && type.IsEnum,
                RejectBlank = rejectBlank && scalar == "string",
                Presence = needed && (!many || indexed) ? Presence.Required : Presence.Optional,
                HasServerDefault = hasDefault,
                ServerDefault = !hasDefault ? null : defaultValue is null ? new JsonNullValue() : ToJsonValue(defaultValue),
            });
            return true;
        }

        void AddObject(Type type, string name, bool many, bool needed, List<FormField> target, HashSet<Type> ancestors, int depth, string? wireName = null, bool collectionElement = false)
        {
            var children = new List<FormField>();
            Walk(type, "", children, new HashSet<Type>(ancestors), depth + 1, many || collectionElement);
            count++;
            target.Add(new FormField
            {
                Name = name,
                Kind = "object",
                Fields = children,
                Repeated = many,
                Indexed = many,
                WireName = wireName,
                Presence = needed ? Presence.Required : Presence.Optional
            });
        }

        void AddMember(Type type, string name, bool needed, bool constructor, List<FormField> target, HashSet<Type> ancestors, int depth, bool collectionElement = false)
        {
            var (item, many) = UnwrapCollection(type);
            if (type.IsArray && type.GetArrayRank() != 1) { Unsupported("multidimensional form arrays need an explicit binding"); return; }
            if (type == typeof(IFormFileCollection)) { Unsupported("nested IFormFileCollection reads every file; use IReadOnlyList<IFormFile> for named files"); return; }
            if (collectionElement && type == typeof(IReadOnlyList<IFormFile>))
            {
                Unsupported("file-list converters always report a value, preventing a surrounding model collection from terminating; use single IFormFile members"); return;
            }
            if (many && item == typeof(IFormFile) && type != typeof(IReadOnlyList<IFormFile>))
            {
                Unsupported("named file collections require IReadOnlyList<IFormFile>"); return;
            }
            if (TryLeaf(item, name, many, indexed: item != typeof(IFormFile), needed, target)) { return; }
            if (many || constructor) { AddObject(item, name, many, needed, target, ancestors, depth, collectionElement: collectionElement); }
            else { Walk(type, name, target, new HashSet<Type>(ancestors), depth + 1, collectionElement); }
        }

        void Walk(Type type, string prefix, List<FormField> target, HashSet<Type> ancestors, int depth, bool collectionElement = false)
        {
            var constructors = type.GetConstructors();
            if (!type.IsClass || type.IsAbstract || typeof(System.Collections.IEnumerable).IsAssignableFrom(type)
                || constructors.Length != 1
                || type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IParsable<>))
                || !ancestors.Add(type) || depth > 8 || count > 256)
            {
                Unsupported("nested forms require finite, non-recursive models with one public constructor; custom parsers need an explicit binding");
                return;
            }
            var before = target.Count;
            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0 && p.GetMethod?.IsPublic == true).ToArray();
            var parameters = constructors[0].GetParameters();
            foreach (var argument in parameters)
            {
                var property = properties.FirstOrDefault(p => p.Name.Equals(argument.Name, StringComparison.OrdinalIgnoreCase));
                var member = property?.GetCustomAttribute<DataMemberAttribute>();
                var component = member is { IsNameSetExplicitly: true, Name: not null } ? member.Name : argument.Name!;
                var name = prefix.Length == 0 ? component : prefix + "." + component;
                // Every constructor parameter is required, including parameters with CLR defaults.
                if (component.Length == 0 || component.IndexOfAny(['.', '[', ']']) >= 0)
                {
                    Unsupported("form member names cannot contain path or index delimiters"); continue;
                }
                AddMember(argument.ParameterType, name, true, true, target, ancestors, depth, collectionElement);
            }
            foreach (var property in properties.OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                if (parameters.Any(p => p.Name!.Equals(property.Name, StringComparison.OrdinalIgnoreCase)) || property.SetMethod?.IsPublic != true
                    || property.IsDefined(typeof(IgnoreDataMemberAttribute))) { continue; }
                var member = property.GetCustomAttribute<DataMemberAttribute>();
                var component = member is { IsNameSetExplicitly: true, Name: not null } ? member.Name : property.Name;
                if (component.Length == 0 || component.IndexOfAny(['.', '[', ']']) >= 0) { Unsupported("form member names cannot contain path or index delimiters"); continue; }
                var name = prefix.Length == 0 ? component : prefix + "." + component;
                var needed = property.IsDefined(typeof(RequiredMemberAttribute)) || member is { IsNameSetExplicitly: true, Name: not null, IsRequired: true };
                AddMember(property.PropertyType, name, needed, false, target, ancestors, depth, collectionElement);
            }
            if (target.Count == before) { Unsupported("form model has no supported writable fields"); }
        }

        void Unsupported(string reason) => bag.Error(TisiliaCodes.ParameterRouteMismatch, "SV30", path,
            $"operation '{operationId}': form parameter '{parameter.Name}': {reason}", [operationId]);
    }
}
