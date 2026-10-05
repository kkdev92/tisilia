using Tisilia.AspNetCore.Bindings;

namespace Tisilia.AspNetCore;

public enum TisiliaSelection
{
    /// <summary>Only endpoints registered with <c>WithTisiliaOperation</c> / <c>[TisiliaOperation]</c> are exported.</summary>
    Explicit,
}

/// <summary>Registration options. Registration never changes JSON, authentication or CORS settings.</summary>
public sealed class TisiliaOptions
{
    /// <summary>Stable API identifier used as <c>contract.apiId</c>.</summary>
    public string ApiId { get; set; } = "";

    public TisiliaSelection Selection { get; set; } = TisiliaSelection.Explicit;

    /// <summary>Route of the contract endpoint mapped by <c>MapTisiliaContract</c>.</summary>
    public string ContractRoute { get; set; } = "/__tisilia/contract";

    /// <summary>Route prefix of the Explorer assets mapped by <c>MapTisiliaExplorer</c>.</summary>
    public string ExplorerRoute { get; set; } = "/__tisilia";

    /// <summary>Outside Development the contract/Explorer endpoints require both this flag and <see cref="AuthorizationPolicy"/>.</summary>
    public bool AllowProduction { get; set; }

    /// <summary>Authorization policy name applied to the contract and Explorer endpoints. Required outside Development.</summary>
    public string? AuthorizationPolicy { get; set; }

    /// <summary>Explicit codec bindings for custom converters. Unknown converters are export errors, never <c>any</c>.</summary>
    public CodecBindingCollection Codecs { get; } = new();

    /// <summary>Explicit behavior registrations for Populate / initializer / callback effects. An unregistered effect is an export error.</summary>
    public BehaviorBindingCollection Behaviors { get; } = new();

    /// <summary>Resolver bindings: custom <c>IJsonTypeInfoResolver</c>s and contract modifiers of a profile's resolver chain.</summary>
    public ResolverBindingCollection Resolvers { get; } = new();

    /// <summary>
    /// The declared wire of <see cref="DateTime"/> values: System.Text.Json writes a DateTime according to its runtime
    /// Kind, so the kind each position holds is declared — a default and member exceptions. Undeclared DateTimes are export errors.
    /// </summary>
    public DateTimeBindingCollection DateTimes { get; } = new();

    /// <summary>Documentation entries copied to <c>contract.documentation</c> (excluded from the semantic hash).</summary>
    public Dictionary<string, (string Summary, string Description)> Documentation { get; } = new(StringComparer.Ordinal);

    /// <summary>Name of the response header carrying the semantic hash when <see cref="EmitSemanticHashHeader"/> is enabled (opt-in).</summary>
    public string SemanticHashHeader { get; set; } = "x-tisilia-contract";

    public bool EmitSemanticHashHeader { get; set; }
}
