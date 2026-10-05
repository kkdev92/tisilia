using System.Globalization;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Tisilia.Contract;

namespace Tisilia.AspNetCore.Export;

internal static class ResolvedRoutes
{
    public static RoutePlan Build(RoutePattern pattern, IReadOnlyList<Parameter> parameters, ParameterPolicyFactory policyFactory)
    {
        return new RoutePlan
        {
            Segments = pattern.PathSegments.Select(segment => new RouteSegment
            {
                Parts = segment.Parts.Select<RoutePatternPart, RoutePart>(part => part switch
                {
                    RoutePatternLiteralPart literal => new RouteLiteral { Value = literal.Content },
                    RoutePatternSeparatorPart separator => new RouteSeparator { Value = separator.Content },
                    RoutePatternParameterPart parameter => Parameter(parameter),
                    _ => throw new NotSupportedException("Unknown resolved route part"),
                }).ToArray(),
            }).ToArray(),
        };

        RouteParameter Parameter(RoutePatternParameterPart part)
        {
            var parameter = parameters.SingleOrDefault(p => p.Location == ParameterLocation.Path && p.Name.Equals(part.Name, StringComparison.OrdinalIgnoreCase))
                ?? throw new NotSupportedException($"Route parameter '{part.Name}' has no uniquely resolved binder");
            foreach (var policy in part.ParameterPolicies)
            {
                if (policyFactory.Create(part, policy) is IOutboundParameterTransformer)
                {
                    throw new NotSupportedException($"Route parameter '{part.Name}' requires an outbound transformer; incoming-only constraints are supported");
                }
            }
            var hasDefault = pattern.Defaults.TryGetValue(part.Name, out var value);
            if (value is not null && value is not (string or bool or char or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal or Guid))
            {
                throw new NotSupportedException($"Route parameter '{part.Name}' has a non-scalar runtime default");
            }
            return new RouteParameter
            {
                ParameterId = parameter.Id,
                Name = part.Name,
                Optional = part.IsOptional,
                CatchAll = !part.IsCatchAll ? CatchAllKind.None : part.EncodeSlashes ? CatchAllKind.EncodeSlashes : CatchAllKind.PreserveSlashes,
                HasDefault = hasDefault,
                DefaultValue = hasDefault ? Convert.ToString(value, CultureInfo.InvariantCulture) : null,
                Policies = part.ParameterPolicies.Select(p => p.Content ?? p.ParameterPolicy?.GetType().FullName ?? throw new NotSupportedException("Unresolved route policy")).ToArray(),
            };
        }
    }
}
