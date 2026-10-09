using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tisilia.AspNetCore;
using Tisilia.AspNetCore.Bindings;
using Tisilia.AspNetCore.Export;
using Tisilia.Generator.Building;
using Tisilia.Generator.Closure;
using Tisilia.Generator.Conformance;
using Xunit;

namespace Tisilia.Contract.Tests;

/// <summary>
/// System.Text.Json reads a DateTime written with an offset as <c>DateTimeOffset.LocalDateTime</c>, the server's own time
/// (JsonHelpers.Date.cs and DateTime.ToLocalTime, v10.0.0), and DateTime dictionary keys are equal by their ticks, so keys a client writes
/// differently can be one key on the server. Oracles: the declared zone's table against TimeZoneInfo itself, and keys sent through the
/// runtime's interpreter to Kestrel.
/// </summary>
public sealed class ServerTimeZoneTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tisilia-server-zone-" + Guid.NewGuid().ToString("N"));

    public ServerTimeZoneTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    // by IANA id, or by Windows id where the process cannot convert one to the other (invariant globalization on Windows)
    public static TheoryData<string, string> Zones => new()
    {
        { "America/Whitehorse", "Yukon Standard Time" },
        { "America/Caracas", "Venezuela Standard Time" },
        { "Asia/Amman", "Jordan Standard Time" },
        { "Europe/Berlin", "W. Europe Standard Time" },
        { "Europe/London", "GMT Standard Time" },
        { "Australia/Sydney", "AUS Eastern Standard Time" },
        { "Australia/Lord_Howe", "Lord Howe Standard Time" },
        { "Pacific/Apia", "Samoa Standard Time" },
        { "Africa/Casablanca", "Morocco Standard Time" },
        { "Asia/Kathmandu", "Nepal Standard Time" },
        { "Asia/Tokyo", "Tokyo Standard Time" },
        { "Etc/UTC", "UTC" },
    };

    private static TimeZoneInfo FindZone(string iana, string windows)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(iana); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById(windows); }
    }

    [Theory]
    [MemberData(nameof(Zones))]
    public void The_table_gives_each_instant_the_offset_TimeZoneInfo_gives(string iana, string windows)
    {
        var zone = FindZone(iana, windows);
        var table = ServerTimeZoneTable.Of(zone);
        Assert.Equal(ServerTimeZoneTable.FromTicks, table.Offsets[0].StartTicks);
        Assert.All(table.Offsets.Zip(table.Offsets.Skip(1)), p => Assert.True(p.First.StartTicks < p.Second.StartTicks && p.First.OffsetMinutes != p.Second.OffsetMinutes));

        // each change and the tick before it, the first ticks of each year shifted by every offset of the zone (where .NET's rules give
        // some zones a different offset for a single tick), and random instants
        var offsets = table.Offsets.Select(o => o.OffsetMinutes * TimeSpan.TicksPerMinute).Append(zone.BaseUtcOffset.Ticks).Distinct().ToArray();
        var instants = table.Offsets.SelectMany(o => new[] { o.StartTicks - 1, o.StartTicks }).ToList();
        for (var year = 1900; year <= 2200; year++)
        {
            foreach (var offset in offsets)
            {
                for (var delta = -2L; delta <= 2; delta++) { instants.Add(new DateTime(year, 1, 1).Ticks - offset + delta); }
            }
        }

        var random = new Random(1900);
        for (var i = 0; i < 20_000; i++) { instants.Add(random.NextInt64(ServerTimeZoneTable.FromTicks, ServerTimeZoneTable.UntilTicks)); }
        var mismatches = instants.Where(t => t >= ServerTimeZoneTable.FromTicks && t < ServerTimeZoneTable.UntilTicks)
            .Where(t => zone.GetUtcOffset(new DateTimeOffset(t, TimeSpan.Zero)).Ticks != table.OffsetMinutesAt(t) * TimeSpan.TicksPerMinute)
            .Select(t => new DateTime(t, DateTimeKind.Utc).ToString("O", System.Globalization.CultureInfo.InvariantCulture)).Take(5).ToArray();
        Assert.True(mismatches.Length == 0, zone.Id + ": " + string.Join(", ", mismatches));

        // the binding carries exactly the table
        using var value = JsonDocument.Parse(Assert.Single(table.ToBinding().Context).Value);
        Assert.Equal(zone.Id, value.RootElement.GetProperty("zone").GetString());
        Assert.Equal(ServerTimeZoneTable.UntilTicks.ToString(System.Globalization.CultureInfo.InvariantCulture), value.RootElement.GetProperty("until").GetString());
        Assert.Equal(table.Offsets.Select(o => (o.StartTicks.ToString(System.Globalization.CultureInfo.InvariantCulture), o.OffsetMinutes)),
            value.RootElement.GetProperty("offsets").EnumerateArray().Select(e => (e[0].GetString()!, e[1].GetInt32())));
    }

    [Fact]
    public async Task The_contract_binds_the_DateTime_codecs_to_the_declared_zone()
    {
        var berlin = FindZone("Europe/Berlin", "W. Europe Standard Time");
        await using var app = await StartAsync(o => o.DateTimes.ServerTimeZone = berlin);
        var export = Export(app);
        var binding = export.Index!.Bindings[ServerTimeZoneTable.BindingId];
        Assert.Equal(ServerTimeZoneTable.Of(berlin).ToContextValue(), Assert.Single(binding.Context).Value);
        Assert.Equal(ServerTimeZoneTable.BindingId, export.Index.Codecs["std.datetime.codec"].BindingId);
        // the hour that repeats when daylight saving time ends in Berlin (the guide's example), from .NET's own zone data
        var results = await InterpreterRequests.SendAsync(_dir, export.Text!, app.Urls.Single(), [
            ("keys", Body(Keys(("2026-10-25T02:30:00+02:00", 1), ("2026-10-25T02:30:00+01:00", 2)))),
            ("keys", Body(Keys(("2026-10-25T01:30:00+02:00", 1), ("2026-10-25T01:30:00+01:00", 2)))),
        ]);
        Assert.StartsWith("key-collision: ", results[0], StringComparison.Ordinal);
        Assert.Contains("'" + berlin.Id + "'", results[0], StringComparison.Ordinal);
        Assert.StartsWith("200 ", results[1], StringComparison.Ordinal);
        var report = app.Services.GetRequiredService<TisiliaContractExporter>().Diagnose();
        Assert.Equal(berlin.Id, report.DateTimeServerTimeZone);
        Assert.Equal(ServerTimeZoneTable.Of(berlin).HasSameOffsets(ServerTimeZoneTable.Of(TimeZoneInfo.Local)), report.DateTimeServerTimeZoneIsLocal);

        await using var plain = await StartAsync(_ => { });
        var undeclared = Export(plain);
        Assert.DoesNotContain(ServerTimeZoneTable.BindingId, undeclared.Index!.Bindings.Keys);
        Assert.Equal("tisilia.binding.datetime@0.1", undeclared.Index.Codecs["std.datetime.codec"].BindingId);
        Assert.NotEqual(undeclared.Index.Document.SemanticHash, export.Index.Document.SemanticHash);
        Assert.Null(plain.Services.GetRequiredService<TisiliaContractExporter>().Diagnose().DateTimeServerTimeZoneIsLocal);
    }

    [Fact]
    public async Task With_the_server_zone_the_client_sends_the_keys_the_server_keeps_apart_and_refuses_those_it_reads_as_one()
    {
        await using var app = await StartAsync(o => o.DateTimes.ServerTimeZone = TimeZoneInfo.Local);
        var export = Export(app);
        var instant = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
        var local = TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.Local).DateTime;
        var apart = Keys((Text(instant), 1), (Text(instant.AddHours(2)), 2), (Text(local.AddHours(5)), 3));
        var one = Keys((Text(instant), 1), (Text(local), 2));
        var results = await InterpreterRequests.SendAsync(_dir, export.Text!, app.Urls.Single(), [("keys", Body(apart)), ("keys", Body(one))]);

        // the server kept the three keys, at the ticks the table gives them
        Assert.StartsWith("200 ", results[0], StringComparison.Ordinal);
        Assert.Equal([local.Ticks, TimeZoneInfo.ConvertTime(instant.AddHours(2), TimeZoneInfo.Local).Ticks, local.AddHours(5).Ticks], Read(results[0]).Select(k => k.Ticks));
        // a key with an offset and a key without one that the server reads as the same DateTime
        Assert.StartsWith("key-collision: ", results[1], StringComparison.Ordinal);
        Assert.Equal([(local.Ticks, 2)], (await PostAsync(app, one)).Select(k => (k.Ticks, k.Value)));
    }

    [Fact]
    public async Task Without_the_server_zone_the_client_sends_only_keys_no_zone_can_make_one()
    {
        await using var app = await StartAsync(_ => { });
        var export = Export(app);
        var instant = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
        var results = await InterpreterRequests.SendAsync(_dir, export.Text!, app.Urls.Single(), [
            ("keys", Body(Keys((Text(instant), 1), (Text(instant.AddHours(2)), 2)))),
            ("keys", Body(Keys((Text(instant), 1), (Text(instant.AddHours(29)), 2), (Text(instant.UtcDateTime.AddHours(-15)), 3)))),
        ]);
        Assert.StartsWith("unsupported: ", results[0], StringComparison.Ordinal);
        Assert.Contains("TisiliaOptions.DateTimes.ServerTimeZone", results[0], StringComparison.Ordinal);
        Assert.StartsWith("200 ", results[1], StringComparison.Ordinal);
        Assert.Equal(3, Read(results[1]).Length);
    }

    [Fact]
    public async Task Local_keys_of_one_instant_written_with_different_offsets_are_refused()
    {
        // DateTimes.Default = Local narrows the keys to datetime-local-wire
        await using var app = await StartAsync(o => o.DateTimes.Default = DateTimeWire.Local);
        var export = Export(app);
        var same = Keys(("2026-01-01T09:00:00+09:00", 1), ("2026-01-01T01:00:00+01:00", 2));
        var results = await InterpreterRequests.SendAsync(_dir, export.Text!, app.Urls.Single(), [("keys", Body(same))]);
        Assert.StartsWith("key-collision: ", results[0], StringComparison.Ordinal);
        // the server reads both as one key and keeps the last value
        Assert.Equal([2], (await PostAsync(app, same)).Select(k => k.Value));
    }

    [Fact]
    public async Task The_conformance_suite_keeps_Local_dictionary_keys_more_than_28_hours_apart()
    {
        // the client sends them with or without a declared zone, and no zone makes two of them one key
        await using var app = await StartAsync(o => o.DateTimes.Default = DateTimeWire.Local);
        var export = Export(app);
        var closure = ContractClosure.Compute(export.Index!, ["keys"]);
        var several = 0;
        for (var seed = 1UL; seed <= 20; seed++)
        {
            var suite = SuiteBuilder.Build(export.Index!, closure, new SuiteOptions { Seed = seed, CasesPerCodec = 12, TimeZoneId = TimeZoneInfo.Local.Id });
            foreach (var map in suite.Cases.OfType<RequestRoundTripCase>().Where(c => export.Index!.Types[export.Index.Codecs[c.CodecId].TypeId].Shape is MapShape).Select(c => (JsonObjectValue)c.Domain))
            {
                var instants = map.Entries.Select(e => DateTimeOffset.Parse(e.Name, System.Globalization.CultureInfo.InvariantCulture).UtcTicks).Order().ToArray();
                several += instants.Length > 1 ? 1 : 0;
                Assert.All(instants.Zip(instants.Skip(1)), p => Assert.True(p.Second - p.First > 28 * TimeSpan.TicksPerHour, $"seed {seed}: keys {TimeSpan.FromTicks(p.Second - p.First)} apart"));
            }
        }

        Assert.True(several > 0);
    }

    [Fact]
    public async Task Outside_Development_a_server_in_another_zone_logs_a_warning()
    {
        var local = ServerTimeZoneTable.Of(TimeZoneInfo.Local);
        var other = new[] { FindZone("Asia/Tokyo", "Tokyo Standard Time"), FindZone("Europe/Berlin", "W. Europe Standard Time") }
            .First(z => !ServerTimeZoneTable.Of(z).HasSameOffsets(local));
        var logs = new ConcurrentQueue<string>();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders(); builder.Logging.AddProvider(new Recording(logs));
        builder.Services.AddTisilia(o => { o.ApiId = "zone-warning"; o.DateTimes.ServerTimeZone = other; });
        await using var app = builder.Build();
        await app.StartAsync();
        for (var i = 0; i < 100 && !logs.Any(l => l.Contains("declared server time zone", StringComparison.Ordinal)); i++) { await Task.Delay(100); }
        var warning = Assert.Single(logs, l => l.Contains("declared server time zone", StringComparison.Ordinal));
        Assert.Contains("'" + other.Id + "'", warning, StringComparison.Ordinal);
    }

    private sealed class Recording(ConcurrentQueue<string> logs) : ILoggerProvider, ILogger
    {
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) { logs.Enqueue(formatter(state, exception)); }
        }
    }

    public sealed record KeyRead(long Ticks, string Kind, int Value);

    private static async Task<WebApplication> StartAsync(Action<TisiliaOptions> configure)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddTisilia(o => { o.ApiId = "server-zone"; configure(o); });
        var app = builder.Build();
        app.MapPost("/keys", (Dictionary<DateTime, int> keys) => keys.Select(k => new KeyRead(k.Key.Ticks, k.Key.Kind.ToString(), k.Value)).ToArray()).WithTisiliaOperation("keys");
        await app.StartAsync();
        return app;
    }

    private static ExportResult Export(WebApplication app)
    {
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        return export;
    }

    // System.Text.Json's own text of a value: a DateTimeOffset with its offset, a DateTime by its Kind
    private static string Text(DateTimeOffset value) => JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(value))!;
    private static string Text(DateTime value) => JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(value))!;

    private static string Keys(params (string Key, int Value)[] keys) => "{" + string.Join(",", keys.Select(k => JsonSerializer.Serialize(k.Key) + ":" + k.Value)) + "}";

    private static object Body(string json) => new { body = new Dictionary<string, string> { ["$json"] = json } };

    private static KeyRead[] Read(string result) => JsonSerializer.Deserialize<KeyRead[]>(result[(result.IndexOf(' ', StringComparison.Ordinal) + 1)..], JsonSerializerOptions.Web)!;

    private static async Task<KeyRead[]> PostAsync(WebApplication app, string json)
    {
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var response = await client.PostAsync("/keys", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(200, (int)response.StatusCode);
        return JsonSerializer.Deserialize<KeyRead[]>(await response.Content.ReadAsStringAsync(), JsonSerializerOptions.Web)!;
    }
}
