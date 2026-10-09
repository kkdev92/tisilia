using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Tisilia.Contract;
using Tisilia.Generator.Building;
using Tisilia.Generator.Diagnostics;
using Tisilia.Generator.Validation;

namespace Tisilia.AspNetCore.Export;

public sealed partial class TisiliaContractExporter
{
    // FormValueProviderFactory reads form values with CultureInfo.CurrentCulture (aspnetcore v10.0.0)
    private const string MvcValueBinder = "MVC binds this form value through the type's TypeConverter or TryParse with the request culture";

    private static bool IsDictionary(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() is var definition
        && (definition == typeof(Dictionary<,>) || definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>))
        || type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>));

    /// <summary>ComplexObjectModelBinder.CanUpdateReadOnlyProperty (aspnetcore v10.0.0): a read-only member of a mutable reference type is updated in place.</summary>
    private static bool CanUpdateReadOnly(Type type) => !type.IsValueType && !type.IsArray && type != typeof(string);

    // everyFile receives the fields the server fills with every file of the request (a minimal API's IFormFileCollection)
    private static void AddFormFields(ApiParameterDescription parameter, bool mvc, bool apiController, IReadOnlyList<IParameterBindingMetadata> bindings, Func<ClrTypeMapper> mapper,
        IModelMetadataProvider? modelMetadata, ContractBuilder builder, DiagnosticBag bag, string operationId, string path, List<FormField> fields, List<FormField> everyFile)
    {
        var count = 0;
        if (mvc && parameter.ModelMetadata?.MetadataKind == ModelMetadataKind.Property)
        {
            AddMvcModel();
            return;
        }

        var info = (parameter.ParameterDescriptor as IParameterInfoParameterDescriptor)?.ParameterInfo;
        var required = parameter.IsRequired || (apiController && parameter.ModelMetadata?.IsRequired == true);
        var parameterType = ParameterClrType(parameter, mvc);
        var (element, repeated) = UnwrapCollection(parameterType);
        if (parameterType == typeof(IFormFileCollection)) { element = typeof(IFormFile); repeated = true; }
        if (parameterType.IsArray && parameterType.GetArrayRank() != 1) { Unsupported("multidimensional form arrays need an explicit binding"); return; }
        if (!mvc && repeated && element == typeof(IFormFile) && parameterType != typeof(IFormFileCollection))
        {
            Unsupported("root file collections require IFormFileCollection; named collections belong on a model"); return;
        }
        var rootIndexed = !mvc && repeated && parameterType != typeof(IFormFileCollection) && !parameterType.IsArray;
        // RequestDelegateFactory binds a parameter whose type, or array element type, has a TryParse (IParsable<T> included) through it,
        // as it binds a query value, not through the form mapper, and records that as HasTryParse (useSimpleBinding, aspnetcore v10.0.0).
        // ApiExplorer describes the parameter from that record, so it is always there for a minimal API.
        var binding = mvc ? null : bindings.FirstOrDefault(b => ReferenceEquals(b.ParameterInfo, info));
        if (TryLeaf(element, parameter.Name, repeated, indexed: rootIndexed, required, fields,
            mvc && parameter.ModelMetadata?.ConvertEmptyStringToNull != false, info?.HasDefaultValue == true, info?.DefaultValue,
            rootIndexed ? "" : null, simpleBinding: binding?.HasTryParse,
            ownParser: mvc && (repeated ? parameter.ModelMetadata?.ElementMetadata : parameter.ModelMetadata)?.IsComplexType == false))
        {
            // RequestDelegateFactory binds an IFormFileCollection parameter to form.Files, every file whatever its name; MVC's
            // FormFileModelBinder picks the files of its own name, ignoring case (aspnetcore v10.0.0)
            if (!mvc && parameterType == typeof(IFormFileCollection)) { everyFile.Add(fields[^1]); }
            return;
        }
        if (mvc)
        {
            // a type MVC reads from one value (a TypeConverter or a TryParse) is not a model
            if (parameter.ModelMetadata?.IsComplexType == false) { Unsupported(MvcValueBinder); return; }
            AddMvcModel();
            return;
        }
        if (binding is null) { Unsupported("the endpoint does not record how it binds this parameter"); return; }
        if (binding.HasTryParse)
        {
            Unsupported("its type parses the form value with its own TryParse, which the contract cannot describe; use a builtin scalar, a type with a registered codec (the additional codecs) or a model"); return;
        }
        // FormDataMapper reads complex parameters at the form root, including root collections and dictionaries.
        if (TryMap(parameterType, parameter.Name, required, fields, wireName: "")) { return; }
        if (repeated) { AddObject(element, parameter.Name, true, required, fields, [], 0, ""); }
        else { Walk(parameterType, "", fields, [], 0); }

        // simpleBinding: whether RequestDelegateFactory reads this root value through the type's TryParse (false: the form mapper reads it;
        // null: the endpoint does not say). ownParser: an MVC root value whose model metadata is not complex, which MVC reads through the
        // type's TypeConverter or TryParse.
        bool TryLeaf(Type type, string name, bool many, bool indexed, bool needed, List<FormField> target, bool rejectBlank = false, bool hasDefault = false, object? defaultValue = null, string? wireName = null, bool? simpleBinding = false, bool ownParser = false)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            var file = type == typeof(IFormFile);
            var scalar = type == typeof(DateTime) ? "datetime" : ClrTypeMapper.ScalarNameOf(type);
            // a module type with a parameter grammar (an additional codec) is written as its request codec's canonical text, as a parameter
            // is; MVC reads it with the request culture, so there it is text its own parser reads, like a type no codec describes
            var paired = file || type.IsEnum || scalar is not null || mvc ? null : mapper().PairedParameter(type, path);
            // A type the server reads from one text with its own parser — a minimal API root value through its TryParse, a member through
            // IParsable<T> (ParsableConverter), an MVC value through its TypeConverter or TryParse — is a string the contract does not check.
            var serverParsed = !file && !type.IsEnum && scalar is null && paired is null
                && (mvc ? ownParser : simpleBinding == true || simpleBinding == false && type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IParsable<>) && i.GetGenericArguments()[0] == type));
            if (serverParsed)
            {
                bag.Warning(TisiliaCodes.ParameterRouteMismatch, "SV30", path, $"operation '{operationId}': form field '{name}' is sent as text that the server parses with {FriendlyName(type)}'s own parser; the contract does not describe which texts it accepts, so the client sends any string and the server answers the texts it refuses", [operationId],
                    "take a builtin scalar or an additional codec type to have the client check the value");
                scalar = "string";
            }
            if (!file && !type.IsEnum && paired is null && (scalar is null or "bytes" or "json-value" or "datetime-local-wire")) { return false; }
            // MVC reads a builtin scalar with the request culture: the client writes it so that every culture reads the same value
            // or refuses it. Strings, enum names and files read the same in every culture.
            var requestCulture = mvc && !file && !type.IsEnum && scalar != "string";
            if (requestCulture && !HttpRules.RequestCultureScalars.Contains(Builtins.Scalar(scalar!)))
            {
                Unsupported(MvcValueBinder); return true;
            }
            if (paired is not null)
            {
                // RequestDelegateFactory reads a root value or array through the type's TryParse, as it reads a query value. The form mapper
                // reads members and other collections through IParsable<T>.TryParse (ParsableConverter) or Uri.TryCreate(RelativeOrAbsolute)
                // (UriFormDataConverter), with the invariant culture either way (aspnetcore v10.0.0); for these types that is the method a
                // query parameter of the type uses. A type without IParsable<T> is a model to the form mapper (Version).
                if (simpleBinding is null) { Unsupported("the endpoint does not record how it binds this parameter"); return true; }
                if (simpleBinding == false && type != typeof(Uri) && !type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IParsable<>) && i.GetGenericArguments()[0] == type))
                {
                    Unsupported($"the form mapper reads {FriendlyName(type)} as a model, not as one value, because it does not implement IParsable<T>; a [FromForm] {FriendlyName(type)} or {FriendlyName(type)}[] parameter binds through its TryParse");
                    return true;
                }
            }
            var use = file ? null : type.IsEnum ? mapper().Map(type, WireDirection.ServerRead, path, isRoot: false) : paired?.Use ?? builder.Scalar(scalar!, NumberProfile.Strict);
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
                RequestCulture = requestCulture,
                GrammarId = paired?.Registration.ParameterGrammarId,
                ServerParsed = serverParsed,
                RejectBlank = rejectBlank && scalar == "string" && !serverParsed,
                Presence = needed && (!many || indexed) ? Presence.Required : Presence.Optional,
                HasServerDefault = hasDefault,
                ServerDefault = !hasDefault ? null : defaultValue is null ? new JsonNullValue() : paired is not null ? paired.Value.Registration.Project?.Invoke(defaultValue) : ToJsonValue(defaultValue),
            });
            return true;
        }

        // FormDataMapper reads a dictionary from name[key], a root one from [key]: the key is the text up to the first ']', parsed with the
        // invariant culture, and keys are gathered ignoring case (DictionaryConverter, FormDataReader.GetKeys, aspnetcore v10.0.0). The
        // client writes each key's canonical text and refuses ']' and keys that are equal ignoring case.
        bool TryMap(Type type, string name, bool needed, List<FormField> target, string? wireName = null)
        {
            if (!type.IsGenericType || type.GetGenericTypeDefinition() is var definition
                && definition != typeof(Dictionary<,>) && definition != typeof(IDictionary<,>) && definition != typeof(IReadOnlyDictionary<,>))
            {
                return false;
            }
            var arguments = type.GetGenericArguments();
            var keyScalar = ClrTypeMapper.ScalarNameOf(arguments[0]);
            if (keyScalar is not ("string" or "int8" or "uint8" or "int16" or "uint16" or "int32" or "uint32" or "int64" or "uint64" or "guid"))
            {
                Unsupported("form dictionary keys must be strings, integers or Guids"); return true;
            }
            var values = new List<FormField>();
            if (!TryLeaf(arguments[1], name, false, indexed: false, true, values) || values is [{ Kind: "file" }])
            {
                Unsupported("form dictionary values must be builtin scalars, enums or values of the additional codec types"); return true;
            }
            if (values is [var value])
            {
                target.Add(value with { Kind = "map", KeyUse = builder.Scalar(keyScalar), WireName = wireName, Presence = needed ? Presence.Required : Presence.Optional });
            }
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
            if (type == typeof(IFormFileCollection))
            {
                // the form mapper fills a member IFormFileCollection with every file of the request too (FormDataMapper's IFormFileCollection
                // converter, aspnetcore v10.0.0): exact as the operation's only file field, which the operation checks; in a model
                // collection each item would take them all
                if (collectionElement) { Unsupported("an IFormFileCollection in a model collection receives every file of the request for each item; use single IFormFile members"); return; }
                if (TryLeaf(typeof(IFormFile), name, true, indexed: false, needed, target)) { everyFile.Add(target[^1]); }
                return;
            }
            if (collectionElement && type == typeof(IReadOnlyList<IFormFile>))
            {
                Unsupported("file-list converters always report a value, preventing a surrounding model collection from terminating; use single IFormFile members"); return;
            }
            if (many && item == typeof(IFormFile) && type != typeof(IReadOnlyList<IFormFile>))
            {
                Unsupported("named file collections require IReadOnlyList<IFormFile>"); return;
            }
            if (TryMap(type, name, needed, target) || TryLeaf(item, name, many, indexed: item != typeof(IFormFile), needed, target)) { return; }
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
                // Every constructor parameter is required, including parameters with CLR defaults. A name with '.', '[' or ']'
                // is read as that exact key; two members that come out as one key are overlapping wire names (SV30).
                if (component.Length == 0)
                {
                    Unsupported("form member names cannot be empty"); continue;
                }
                AddMember(argument.ParameterType, name, true, true, target, ancestors, depth, collectionElement);
            }
            foreach (var property in properties.OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                if (parameters.Any(p => p.Name!.Equals(property.Name, StringComparison.OrdinalIgnoreCase)) || property.SetMethod?.IsPublic != true
                    || property.IsDefined(typeof(IgnoreDataMemberAttribute))) { continue; }
                var member = property.GetCustomAttribute<DataMemberAttribute>();
                var component = member is { IsNameSetExplicitly: true, Name: not null } ? member.Name : property.Name;
                if (component.Length == 0) { Unsupported("form member names cannot be empty"); continue; }
                var name = prefix.Length == 0 ? component : prefix + "." + component;
                var needed = property.IsDefined(typeof(RequiredMemberAttribute)) || member is { IsNameSetExplicitly: true, Name: not null, IsRequired: true };
                AddMember(property.PropertyType, name, needed, false, target, ancestors, depth, collectionElement);
            }
            if (target.Count == before) { Unsupported("form model has no supported writable fields"); }
        }

        // MVC (aspnetcore v10.0.0): ParameterBinder reads a complex model under its BinderModelName when one is set, else under the
        // parameter's name when a key starts with it, else at the root, and ApiExplorer describes the model's leaves without that prefix.
        // The client always writes the prefix, so the model never reads a sibling's keys: the model is one object field named by it,
        // walked through the metadata ComplexObjectModelBinder binds by — the bound constructor's parameters, then the other bindable
        // properties, each under its BinderModelName or own name.
        void AddMvcModel()
        {
            if (parameter.ParameterDescriptor is not { } owner || modelMetadata is null)
            {
                Unsupported("the endpoint does not record the MVC model this field belongs to"); return;
            }
            var ownerInfo = (owner as IParameterInfoParameterDescriptor)?.ParameterInfo;
            var ownerMetadata = ownerInfo is not null && modelMetadata is ModelMetadataProvider provider ? provider.GetMetadataForParameter(ownerInfo) : modelMetadata.GetMetadataForType(owner.ParameterType);
            var prefix = owner.BindingInfo?.BinderModelName ?? ownerMetadata.BinderModelName ?? owner.Name;
            if (prefix.Length == 0) { Unsupported("an MVC form model with an empty prefix reads its sibling fields' keys"); return; }
            var needed = ownerMetadata.IsBindingRequired;
            var filter = owner.BindingInfo?.PropertyFilterProvider?.PropertyFilter;
            if (IsDictionary(ownerMetadata.ModelType)) { Unsupported("MVC form dictionaries need an explicit binding"); return; }
            if (ownerMetadata.IsEnumerableType)
            {
                if (ownerMetadata.ElementMetadata is not { IsComplexType: true } item || item.ModelType == typeof(IFormFile)) { Unsupported("complex MVC forms need an explicit binding"); return; }
                MvcObject(item, prefix, many: true, needed, fields, [], 0, filter);
                return;
            }
            MvcObject(ownerMetadata, prefix, many: false, needed, fields, [], 0, filter);
        }

        void MvcObject(ModelMetadata model, string name, bool many, bool needed, List<FormField> target, HashSet<Type> ancestors, int depth, Func<ModelMetadata, bool>? filter = null)
        {
            var children = new List<FormField>();
            MvcWalk(model, children, new HashSet<Type>(ancestors), depth + 1, filter);
            if (children.Count == 0) { return; }
            count++;
            target.Add(new FormField { Name = name, Kind = "object", Fields = children, Repeated = many, Indexed = many, Presence = needed ? Presence.Required : Presence.Optional });
        }

        void MvcWalk(ModelMetadata model, List<FormField> target, HashSet<Type> ancestors, int depth, Func<ModelMetadata, bool>? filter)
        {
            if (!ancestors.Add(model.ModelType) || depth > 8 || count > 256)
            {
                Unsupported("nested MVC forms require finite, non-recursive models"); return;
            }
            var parameters = model.BoundConstructor?.BoundConstructorParameters ?? [];
            var typeFilter = model.PropertyFilterProvider?.PropertyFilter;
            // ComplexObjectModelBinder.CanBindItem. A [FromServices] member is not request data. A member with another request source
            // ([FromQuery], [FromRoute] …) is read under the model's prefix there, which the names ApiExplorer reports for it lack, and a
            // [ModelBinder] member reads the request in its own code: neither is described.
            bool Bindable(ModelMetadata member)
            {
                if (!member.IsBindingAllowed || typeFilter?.Invoke(member) == false || filter?.Invoke(member) == false
                    || member.BindingSource == BindingSource.Services || member.BindingSource == BindingSource.Special) { return false; }
                if (member.BinderType is not null || member.BindingSource == BindingSource.Custom)
                {
                    Unsupported($"MVC form member '{member.Name}' binds through a [ModelBinder], which reads the request in code the contract cannot describe"); return false;
                }
                if (member.BindingSource is { } source && source != BindingSource.Form && source != BindingSource.FormFile && source != BindingSource.ModelBinding)
                {
                    Unsupported($"MVC form member '{member.Name}' is bound from {source.DisplayName}, under the form model's prefix; move it to its own action parameter"); return false;
                }
                return true;
            }
            var before = target.Count;
            foreach (var member in parameters.Where(Bindable)) { AddMvcMember(member, target, ancestors, depth); }
            foreach (var property in model.Properties)
            {
                // ModelMetadata.BoundProperties: a record's constructor parameter is bound once, as the parameter
                if (parameters.Any(p => string.Equals(p.ParameterName, property.PropertyName, StringComparison.Ordinal) && p.ModelType == property.ModelType)
                    || !Bindable(property) || property.IsReadOnly && !CanUpdateReadOnly(property.ModelType)) { continue; }
                AddMvcMember(property, target, ancestors, depth);
            }
            if (target.Count == before) { Unsupported("form model has no supported writable fields"); }
        }

        void AddMvcMember(ModelMetadata member, List<FormField> target, HashSet<Type> ancestors, int depth)
        {
            var name = member.BinderModelName ?? member.Name!;
            if (name.Length == 0) { Unsupported("form member names cannot be empty"); return; }
            // under [ApiController] a validation-required member that is missing is an automatic 400; [BindRequired] always fails binding
            var needed = member.IsBindingRequired || (apiController && member.IsRequired
                && (member.IsReferenceOrNullableType || member.ValidatorMetadata.OfType<System.ComponentModel.DataAnnotations.RequiredAttribute>().Any()));
            var type = member.ModelType;
            if (IsDictionary(type)) { Unsupported("MVC form dictionaries need an explicit binding"); return; }
            // FormFileModelBinder binds a file member, or every file part of the member's name for a file collection
            if (type == typeof(IFormFile)) { TryLeaf(type, name, false, indexed: false, needed, target); return; }
            if (type == typeof(IFormFileCollection) || UnwrapCollection(type) is ({ } fileItem, true) && fileItem == typeof(IFormFile))
            {
                TryLeaf(typeof(IFormFile), name, true, indexed: false, needed, target); return;
            }
            if (!member.IsComplexType)
            {
                if (!TryLeaf(type, name, false, indexed: false, needed, target, rejectBlank: member.ConvertEmptyStringToNull, ownParser: true)) { Unsupported(MvcValueBinder); }
                return;
            }
            if (member.IsEnumerableType && member.ElementMetadata is { } element)
            {
                // CollectionModelBinder reads repeated values of the member's name for simple items, indexed names for models
                if (!element.IsComplexType)
                {
                    if (!TryLeaf(element.ModelType, name, true, indexed: false, needed, target, rejectBlank: element.ConvertEmptyStringToNull, ownParser: true)) { Unsupported(MvcValueBinder); }
                    return;
                }
                MvcObject(element, name, many: true, needed, target, ancestors, depth);
                return;
            }
            MvcObject(member, name, many: false, needed, target, ancestors, depth);
        }

        void Unsupported(string reason) => bag.Error(TisiliaCodes.ParameterRouteMismatch, "SV30", path,
            $"operation '{operationId}': form parameter '{parameter.Name}': {reason}", [operationId]);
    }
}
