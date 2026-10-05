using System.Text.Json;
using System.Text.Json.Nodes;
using Tisilia.Generator.Validation;

namespace Tisilia.Contract.Tests;

internal static class RouteTestContracts
{
    // Editing a route in a valid 0.4 test document requires editing its executable plan too.
    public static void SetRoute(JsonNode operation, string route)
    {
        operation["route"] = route;
        var parameters = operation["parameters"]!.Deserialize<List<Parameter>>(TisiliaJson.Options)!;
        operation["routePlan"] = JsonSerializer.SerializeToNode(RoutePlans.Parse(route, parameters), TisiliaJson.Options);
    }
}
