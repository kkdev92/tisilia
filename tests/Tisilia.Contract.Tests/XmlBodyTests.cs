using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Xml.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tisilia.AspNetCore;
using Tisilia.AspNetCore.Export;
using Tisilia.Generator.Diagnostics;
using Xunit;

namespace Tisilia.Contract.Tests;

/// <summary>
/// MVC's XmlSerializer formatters read and write a body with XmlSerializer's own mapping, which the contract describes. Oracle: through
/// Kestrel, the server receives what the runtime writes from a domain value (attributes, namespaces, nested and flattened collections,
/// xsi:nil, Specified and ShouldSerialize members, a default value, character content, every builtin scalar form), and the runtime decodes
/// what the server writes, absent members as null, the default or no values; a form the client does not write stays bytes with the reason.
/// </summary>
public sealed class XmlBodyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tisilia-xml-bodies-" + Guid.NewGuid().ToString("N"));

    public XmlBodyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private static async Task<WebApplication> StartAsync(Type controller, Action<IMvcBuilder>? formatters = null, Action<MvcOptions>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        var mvc = builder.Services.AddControllers().ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new OneController(controller)));
        (formatters ?? (b => b.AddXmlSerializerFormatters()))(mvc);
        if (configure is not null)
        {
            builder.Services.PostConfigure(configure);
        }

        builder.Services.AddTisilia(o => o.ApiId = "xml-bodies");
        var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        return app;
    }

    private static ExportResult Export(WebApplication app)
    {
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        return export;
    }

    private const string Order = """
        {
          "id": 7, "Channel": "web", "Customer": "c", "Note": null, "Priority": null, "Total": 1.50, "Ratio": -0.25,
          "PlacedAt": "2026-01-02T03:04:05.5Z", "Updated": "2026-01-02T03:04:05+09:00", "Due": "2026-03-04", "Slot": "05:06:07.1",
          "Window": "1.02:03:04", "Ref": "01234567-89ab-cdef-0123-456789abcdef", "Blob": "AQI=", "Grade": "B", "Color": "bleu", "Access": 3,
          "Lines": [{ "Sku": "a\r\nb", "Quantity": 2 }, null], "tag": ["x", "y"], "Codes": ["c1", null], "Extra": { "Sku": "e", "Quantity": 0 },
          "Discount": 4, "Price": { "Currency": "JPY", "Amount": 120 }
        }
        """;

    [Fact]
    public async Task The_server_reads_what_the_runtime_writes_and_the_runtime_reads_what_the_server_writes()
    {
        await using var app = await StartAsync(typeof(XmlOrdersController));
        var export = Export(app);
        var operations = export.Index!.Operations;
        var request = Assert.IsType<Tisilia.Contract.XmlRequestBody>(operations["xml.orders.echo"].RequestBody);
        Assert.Equal(("application/xml", "order", "urn:shop", 32), (request.MediaType, request.Root.Name, request.Root.Namespace, request.MaxDepth));
        var response = Assert.IsType<Tisilia.Contract.XmlResponseBody>(Assert.Single(operations["xml.orders.echo"].Responses).Body);
        Assert.Equal(("application/xml", "order"), (response.MediaType, response.Root.Name));
        Assert.DoesNotContain(export.Diagnostics.Items, d => d.Rule == "SV29");

        var sent = await InterpreterRequests.SendAsync(_dir, export.Text!, app.Urls.Single(), [
            ("xml.orders.report", new { body = new Dictionary<string, string> { ["$json"] = Order } }),
            ("xml.orders.report", new { body = new Dictionary<string, string> { ["$json"] = """{ "id": 1 }""" } }),
        ]);
        // what XmlSerializer made of the request: the CR survives as a character reference, an omitted member keeps the constructor's value
        // (Retries 5; a list is created empty, an array stays null), Discount sent sets DiscountSpecified, a flags value of two constants is All
        Assert.Equal("200 id=7 channel=web customer=c note=null priority=null total=1.50 ratio=-0.25 placed=2026-01-02T03:04:05.5000000Z/Utc updated=2026-01-02T03:04:05.0000000+09:00 due=2026-03-04 slot=05:06:07.1000000 window=1.02:03:04 ref=01234567-89ab-cdef-0123-456789abcdef blob=0102 grade=B color=Blue access=All lines=[a\\r\\nb:2,null] tags=[x,y] codes=[c1,null] extra=e:0 retries=5 discount=4/True points=0 price=JPY:120", sent[0]);
        Assert.Equal("200 id=1 channel=null customer=null note=null priority=null total=0 ratio=0 placed=0001-01-01T00:00:00.0000000/Unspecified updated=0001-01-01T00:00:00.0000000+00:00 due=0001-01-01 slot=00:00:00.0000000 window=00:00:00 ref=00000000-0000-0000-0000-000000000000 blob=null grade=\0 color=Red access=None lines=[] tags=[] codes=null extra=null retries=5 discount=0/False points=0 price=null", sent[1]);

        var executed = await InterpreterRequests.SendAsync(_dir, export.Text!, app.Urls.Single(), [
            ("xml.orders.echo", new { body = new Dictionary<string, string> { ["$json"] = Order } }),
            ("xml.orders.echo", new { body = new Dictionary<string, string> { ["$json"] = """{ "id": 1, "Points": 3 }""" } }),
        ], execute: true);
        using var full = JsonDocument.Parse(executed[0]);
        var data = full.RootElement.GetProperty("data");
        Assert.Equal("response", full.RootElement.GetProperty("kind").GetString());
        // the response: the server writes CRLF line breaks of element content as its NewLineChars, which XML reads as LF
        Assert.Equal("a\nb", data.GetProperty("Lines")[0].GetProperty("Sku").GetString());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("Lines")[1].ValueKind);
        Assert.Equal(JsonValueKind.Null, data.GetProperty("Note").ValueKind);
        Assert.Equal(2, data.GetProperty("Color").GetInt32());
        Assert.Equal(3, data.GetProperty("Access").GetInt32());
        Assert.Equal("web", data.GetProperty("Channel").GetString());
        Assert.Equal(5, data.GetProperty("Retries").GetInt32()); // left out as the default, read as it
        Assert.Equal(4, data.GetProperty("Discount").GetInt32());
        Assert.False(data.TryGetProperty("Points", out _)); // ShouldSerializePoints() said no
        Assert.Equal("JPY", data.GetProperty("Price").GetProperty("Currency").GetString());
        Assert.Equal("120", data.GetProperty("Price").GetProperty("Amount").GetProperty("coefficient").GetString());
        Assert.Equal("[\"x\",\"y\"]", data.GetProperty("tag").GetRawText());
        using var sparse = JsonDocument.Parse(executed[1]);
        var empty = sparse.RootElement.GetProperty("data");
        // left out by the server: a null reference reads as null, a collection with no element as no values, an unspecified member stays
        // out; the list the server read was created empty, which it writes as an empty element, while the array stayed null
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("Customer").ValueKind);
        Assert.Equal("[]", empty.GetProperty("Lines").GetRawText());
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("Codes").ValueKind);
        Assert.Equal("[]", empty.GetProperty("tag").GetRawText());
        Assert.False(empty.TryGetProperty("Discount", out _));
        Assert.Equal(3, empty.GetProperty("Points").GetInt32());
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("Price").ValueKind);
    }

    [Fact]
    public async Task Collections_scalars_and_recursive_classes_at_the_root_are_read()
    {
        await using var app = await StartAsync(typeof(XmlOrdersController));
        var export = Export(app);
        var operations = export.Index!.Operations;
        // IEnumerable<T> is written through DelegatingEnumerable<T, T> (ArrayOfT), an int as <int>
        Assert.Equal("ArrayOfXmlLine", Assert.IsType<Tisilia.Contract.XmlResponseBody>(Assert.Single(operations["xml.lines"].Responses).Body).Root.Name);
        Assert.Equal("int", Assert.IsType<Tisilia.Contract.XmlResponseBody>(Assert.Single(operations["xml.count"].Responses).Body).Root.Name);
        Assert.Equal(("text/xml", "string"), Assert.IsType<Tisilia.Contract.XmlResponseBody>(Assert.Single(operations["xml.name"].Responses).Body) is var name ? (name.MediaType, name.Root.Name) : default);
        var executed = await InterpreterRequests.SendAsync(_dir, export.Text!, app.Urls.Single(), [("xml.lines", new { }), ("xml.count", new { }), ("xml.tree", new { }), ("xml.name", new { })], execute: true);
        Assert.Equal("""[{"Sku":"a","Quantity":1},{"Sku":null,"Quantity":2}]""", JsonDocument.Parse(executed[0]).RootElement.GetProperty("data").GetRawText());
        Assert.Equal("5", JsonDocument.Parse(executed[1]).RootElement.GetProperty("data").GetRawText());
        Assert.Equal("""{"Name":"root","Child":{"Name":"child","Child":null}}""", JsonDocument.Parse(executed[2]).RootElement.GetProperty("data").GetRawText());
        Assert.Equal("a<b", JsonDocument.Parse(executed[3]).RootElement.GetProperty("data").GetString());
    }

    [Fact]
    public async Task A_null_return_value_is_the_204_MVC_answers_and_hydrates_like_a_JSON_one()
    {
        await using var app = await StartAsync(typeof(XmlOrdersController));
        var export = Export(app);
        var responses = export.Index!.Operations["xml.maybe"].Responses;
        var noContent = Assert.Single(responses, r => r.Status == 204);
        Assert.IsType<Tisilia.Contract.NoResponseBody>(noContent.Body);
        // no body to keep from the page: browser-safe, as the 204 of a JSON operation; the XML case itself stays server-only
        Assert.Equal(Tisilia.Contract.Hydration.BrowserSafe, noContent.Hydration);
        Assert.Equal(Tisilia.Contract.Hydration.ServerOnly, Assert.Single(responses, r => r.Status == 200).Hydration);
        var executed = await InterpreterRequests.SendAsync(_dir, export.Text!, app.Urls.Single(), [("xml.maybe", new { none = true }), ("xml.maybe", new { none = false })], execute: true);
        Assert.Equal("xml.maybe.no-content", JsonDocument.Parse(executed[0]).RootElement.GetProperty("caseId").GetString());
        Assert.Equal("""{"Sku":"m","Quantity":1}""", JsonDocument.Parse(executed[1]).RootElement.GetProperty("data").GetRawText());
    }

    [Fact]
    public async Task A_body_nested_deeper_than_the_formatter_reads_is_refused_before_it_is_sent()
    {
        await using var app = await StartAsync(typeof(XmlOrdersController), configure: o => o.InputFormatters.OfType<XmlSerializerInputFormatter>().Single().MaxDepth = 3);
        var export = Export(app);
        Assert.Equal(3, Assert.IsType<Tisilia.Contract.XmlRequestBody>(export.Index!.Operations["xml.tree.post"].RequestBody).MaxDepth);
        var sent = await InterpreterRequests.SendAsync(_dir, export.Text!, app.Urls.Single(), [
            ("xml.tree.post", new { body = new Dictionary<string, string> { ["$json"] = """{ "Name": "a", "Child": { "Name": "b" } }""" } }),
            ("xml.tree.post", new { body = new Dictionary<string, string> { ["$json"] = """{ "Name": "a", "Child": { "Child": { "Name": "c" } } }""" } }),
        ]);
        Assert.Equal("200 a>b", sent[0]);
        Assert.StartsWith("limit: request body nests 4 elements; the limit is 3", sent[1], StringComparison.Ordinal);
        // the server refuses that document itself (XmlDictionaryReaderQuotas.MaxDepth)
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var deep = await client.PostAsync("/xml/tree", new StringContent("<XmlTree><Child><Child><Name>c</Name></Child></Child></XmlTree>", System.Text.Encoding.UTF8, "application/xml"));
        Assert.Equal(400, (int)deep.StatusCode);
    }

    [Fact]
    public async Task A_form_the_client_does_not_write_or_another_formatter_stays_bytes_with_the_reason()
    {
        await using var app = await StartAsync(typeof(XmlUnsupportedController));
        var export = Export(app);
        Assert.IsType<Tisilia.Contract.BinaryRequestBody>(export.Index!.Operations["xml.zoo"].RequestBody);
        Assert.Contains(export.Diagnostics.Items, d => d.Rule == "SV29" && d.Severity == DiagnosticSeverity.Warning && d.RelatedIds.Contains("xml.zoo") && d.Message.Contains("derived types ([XmlInclude])", StringComparison.Ordinal));
        Assert.IsType<Tisilia.Contract.BinaryResponseBody>(Assert.Single(export.Index.Operations["xml.choice"].Responses).Body);
        Assert.Contains(export.Diagnostics.Items, d => d.Rule == "SV29" && d.RelatedIds.Contains("xml.choice") && d.Message.Contains("a choice of elements", StringComparison.Ordinal));

        await using var contracts = await StartAsync(typeof(XmlOrdersController), b => b.AddXmlDataContractSerializerFormatters());
        var dataContract = contracts.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(dataContract.Diagnostics.HasErrors, string.Join("\n", dataContract.Diagnostics.Items));
        Assert.IsType<Tisilia.Contract.BinaryRequestBody>(dataContract.Index!.Operations["xml.orders.echo"].RequestBody);
        Assert.Contains(dataContract.Diagnostics.Items, d => d.Rule == "SV29" && d.RelatedIds.Contains("xml.orders.echo") && d.Message.Contains("XmlDataContractSerializerInputFormatter reads it", StringComparison.Ordinal));
    }

    private sealed class OneController(Type controller) : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            feature.Controllers.Clear(); feature.Controllers.Add(controller.GetTypeInfo());
        }
    }
}

[ApiController]
[NonController]
public sealed class XmlOrdersController : ControllerBase
{
    [HttpPost("/xml/orders")]
    [TisiliaOperation("xml.orders.echo")]
    [Consumes("application/xml")]
    [Produces("application/xml")]
    public ActionResult<XmlOrder> Echo([FromBody] XmlOrder order) => order;

    [HttpPost("/xml/orders/report")]
    [TisiliaOperation("xml.orders.report")]
    [Consumes("application/xml")]
    public ActionResult<string> Report([FromBody] XmlOrder order)
    {
        static string Text(string? s) => s is null ? "null" : s.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
        static string List<T>(IEnumerable<T?>? items, Func<T, string> text) => items is null ? "null" : "[" + string.Join(",", items.Select(i => i is null ? "null" : text(i))) + "]";
        var c = System.Globalization.CultureInfo.InvariantCulture;
        return string.Join(" ",
            $"id={order.Id}", $"channel={Text(order.Channel)}", $"customer={Text(order.Customer)}", $"note={Text(order.Note)}", $"priority={(order.Priority is { } p ? p.ToString(c) : "null")}",
            $"total={order.Total.ToString(c)}", $"ratio={order.Ratio.ToString(c)}", $"placed={order.PlacedAt.ToString("O", c)}/{order.PlacedAt.Kind}", $"updated={order.Updated.ToString("O", c)}",
            $"due={order.Due.ToString("O", c)}", $"slot={order.Slot.ToString("O", c)}", $"window={order.Window.ToString("c", c)}", $"ref={order.Ref}", $"blob={(order.Blob is null ? "null" : Convert.ToHexString(order.Blob))}",
            $"grade={order.Grade}", $"color={order.Color}", $"access={order.Access}", $"lines={List(order.Lines, l => Text(l.Sku) + ":" + l.Quantity.ToString(c))}", $"tags={List(order.Tags, Text)}",
            $"codes={List(order.Codes, Text)}", $"extra={(order.Extra is null ? "null" : Text(order.Extra.Sku) + ":" + order.Extra.Quantity.ToString(c))}", $"retries={order.Retries}",
            $"discount={order.Discount}/{order.DiscountSpecified}", $"points={order.Points}", $"price={(order.Price is null ? "null" : Text(order.Price.Currency) + ":" + order.Price.Amount.ToString(c))}");
    }

    [HttpGet("/xml/lines")]
    [TisiliaOperation("xml.lines")]
    [Produces("application/xml")]
    public ActionResult<IEnumerable<XmlLine>> Lines() => new List<XmlLine> { new() { Sku = "a", Quantity = 1 }, new() { Sku = null, Quantity = 2 } };

    // StringOutputFormatter writes text/plain only: for text/xml the XML formatter writes the string as <string>…</string>
    [HttpGet("/xml/name")]
    [TisiliaOperation("xml.name")]
    [Produces("text/xml")]
    public ActionResult<string> Name() => "a<b";

    [HttpGet("/xml/count")]
    [TisiliaOperation("xml.count")]
    [Produces("application/xml")]
    public ActionResult<int> Count() => 5;

    // a null return value: HttpNoContentOutputFormatter answers 204 before the XML formatter is asked
    [HttpGet("/xml/maybe")]
    [TisiliaOperation("xml.maybe")]
    [Produces("application/xml")]
    public ActionResult<XmlLine?> Maybe(bool none) => none ? null : new XmlLine { Sku = "m", Quantity = 1 };

    [HttpGet("/xml/tree")]
    [TisiliaOperation("xml.tree")]
    [Produces("application/xml")]
    public ActionResult<XmlTree> Tree() => new XmlTree { Name = "root", Child = new XmlTree { Name = "child" } };

    [HttpPost("/xml/tree")]
    [TisiliaOperation("xml.tree.post")]
    [Consumes("application/xml")]
    public ActionResult<string> PostTree([FromBody] XmlTree tree)
    {
        var names = new List<string>();
        for (var node = tree; node is not null; node = node.Child)
        {
            names.Add(node.Name ?? "?");
        }

        return string.Join(">", names);
    }
}

[ApiController]
[NonController]
public sealed class XmlUnsupportedController : ControllerBase
{
    [HttpPost("/xml/zoo")]
    [TisiliaOperation("xml.zoo")]
    [Consumes("application/xml")]
    public ActionResult<int> Zoo([FromBody] XmlZoo zoo) => zoo.Animal is null ? 0 : 1;

    [HttpGet("/xml/choice")]
    [TisiliaOperation("xml.choice")]
    [Produces("application/xml")]
    public ActionResult<XmlChoice> Choice() => new XmlChoice { Value = 1 };
}

[XmlRoot("order", Namespace = "urn:shop")]
public sealed class XmlOrder
{
    [XmlAttribute("id")] public int Id { get; set; }
    [XmlAttribute] public string? Channel { get; set; }
    public string? Customer { get; set; }
    [XmlElement(IsNullable = true)] public string? Note { get; set; }
    public int? Priority { get; set; }
    public decimal Total { get; set; }
    public double Ratio { get; set; }
    public DateTime PlacedAt { get; set; }
    public DateTimeOffset Updated { get; set; }
    public DateOnly Due { get; set; }
    public TimeOnly Slot { get; set; }
    public TimeSpan Window { get; set; }
    public Guid Ref { get; set; }
    public byte[]? Blob { get; set; }
    public char Grade { get; set; }
    public XmlColor Color { get; set; }
    public XmlAccess Access { get; set; }
    [XmlArray("Lines"), XmlArrayItem("line")] public List<XmlLine?>? Lines { get; set; }
    [XmlElement("tag")] public List<string> Tags { get; set; } = [];
    public string?[]? Codes { get; set; }
    [XmlElement(Namespace = "urn:ext")] public XmlLine? Extra { get; set; }
    [DefaultValue(5)] public int Retries { get; set; } = 5;
    public int Discount { get; set; }
    [XmlIgnore] public bool DiscountSpecified { get; set; }
    public int Points { get; set; }
    public XmlPrice? Price { get; set; }

    public bool ShouldSerializePoints() => Points > 0;
}

public sealed class XmlLine
{
    public string? Sku { get; set; }
    public int Quantity { get; set; }
}

public sealed class XmlPrice
{
    [XmlAttribute] public string? Currency { get; set; }
    [XmlText] public decimal Amount { get; set; }
}

public sealed class XmlTree
{
    public string? Name { get; set; }
    public XmlTree? Child { get; set; }
}

public enum XmlColor
{
    Red,
    Green,
    [XmlEnum("bleu")] Blue,
}

[Flags]
public enum XmlAccess
{
    None = 0,
    Read = 1,
    Write = 2,
    All = 3,
}

public sealed class XmlZoo
{
    public XmlAnimal? Animal { get; set; }
}

[XmlInclude(typeof(XmlDog))]
public class XmlAnimal
{
    public string? Name { get; set; }
}

public sealed class XmlDog : XmlAnimal
{
    public bool Good { get; set; }
}

public sealed class XmlChoice
{
    [XmlElement("number", typeof(int)), XmlElement("text", typeof(string))]
    public object? Value { get; set; }
}
