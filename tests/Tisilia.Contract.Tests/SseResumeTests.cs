using System.Globalization;
using System.Net;
using System.Net.ServerSentEvents;
using System.Net.Sockets;
using System.Text;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tisilia.AspNetCore;
using Tisilia.AspNetCore.Export;
using Tisilia.Generator.Diagnostics;
using Xunit;

namespace Tisilia.Contract.Tests;

/// <summary>
/// An endpoint that declares TisiliaEventResume continues after the event whose id a reconnecting client sends in Last-Event-ID
/// (HTML Standard, server-sent events). Oracle: through Kestrel, a stream the server drops after two events is resumed by the
/// runtime's subscribe, and every event arrives once, in order.
/// </summary>
public sealed class SseResumeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tisilia-sse-resume-" + Guid.NewGuid().ToString("N"));

    public SseResumeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private static async IAsyncEnumerable<SseItem<int>> Ticks(int after)
    {
        for (var i = after + 1; i <= 5; i++)
        {
            await Task.Yield();
            yield return new SseItem<int>(i) { EventId = i.ToString(CultureInfo.InvariantCulture), ReconnectionInterval = TimeSpan.FromMilliseconds(10) };
        }
    }

    /// <summary>
    /// A loopback TCP relay to the application that delivers the first connection's response up to the end of its second event
    /// and then closes it: the client has the two events, and the response ends in the middle, as when a connection drops. A
    /// request aborted in Kestrel instead loses the output it has not sent yet, so a client under load could see the drop before
    /// any event. Later connections pass through.
    /// </summary>
    private sealed class DroppingRelay : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Uri _upstream;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _accepting;
        private int _connections;

        public DroppingRelay(string upstream)
        {
            _upstream = new Uri(upstream);
            _listener.Start();
            _accepting = AcceptAsync();
        }

        public string Url => "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture);

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            try { await _accepting; }
            catch (OperationCanceledException) { }
            _stop.Dispose();
        }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { return; }
                _ = RelayAsync(client, drop: Interlocked.Increment(ref _connections) == 1);
            }
        }

        private async Task RelayAsync(TcpClient client, bool drop)
        {
            using (client)
            using (var upstream = new TcpClient())
            {
                try
                {
                    await upstream.ConnectAsync(_upstream.Host, _upstream.Port, _stop.Token);
                    _ = client.GetStream().CopyToAsync(upstream.GetStream(), _stop.Token);
                    var buffer = new byte[16384];
                    var forwarded = new StringBuilder();
                    int read;
                    while ((read = await upstream.GetStream().ReadAsync(buffer, _stop.Token)) > 0)
                    {
                        var start = forwarded.Length;
                        forwarded.Append(Encoding.Latin1.GetString(buffer, 0, read));
                        var cut = drop ? SecondEventEnd(forwarded.ToString()) - start : -1;
                        if (cut >= 0)
                        {
                            await client.GetStream().WriteAsync(buffer.AsMemory(0, cut), _stop.Token);
                            client.Client.Shutdown(SocketShutdown.Send);
                            return;
                        }

                        await client.GetStream().WriteAsync(buffer.AsMemory(0, read), _stop.Token);
                    }
                }
                catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
                {
                }
            }
        }

        // the end of the second event (each ends with a blank line) after the response head, or -1
        private static int SecondEventEnd(string forwarded)
        {
            var head = forwarded.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (head < 0)
            {
                return -1;
            }

            var first = forwarded.IndexOf("\n\n", head + 4, StringComparison.Ordinal);
            var second = first < 0 ? -1 : forwarded.IndexOf("\n\n", first + 2, StringComparison.Ordinal);
            return second < 0 ? -1 : second + 2;
        }
    }

    [Fact]
    public async Task A_dropped_stream_of_a_resuming_endpoint_reconnects_with_Last_Event_ID()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddTisilia(o => o.ApiId = "sse-resume");
        await using var app = builder.Build();
        var connections = 0;
        var resumedFrom = new List<string>();
        app.MapGet("/ticks", (HttpContext context) =>
        {
            Interlocked.Increment(ref connections);
            var lastEventId = context.Request.Headers["Last-Event-ID"].ToString();
            lock (resumedFrom) { resumedFrom.Add(lastEventId); }
            return TypedResults.ServerSentEvents(Ticks(int.TryParse(lastEventId, CultureInfo.InvariantCulture, out var after) ? after : 0));
        }).WithTisiliaEventResume().WithTisiliaOperation("ticks");
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
        Assert.Equal("last-event-id", Assert.IsType<SseResponseBody>(Assert.Single(export.Index!.Operations["ticks"].Responses).Body).Resume);

        await using var relay = new DroppingRelay(app.Urls.Single());
        var results = await InterpreterRequests.SendAsync(_dir, export.Text!, relay.Url, [
            ("ticks", new { reconnect = new { maxAttempts = 3, delayMs = 5000 } }),
        ], subscribe: true);
        using var outcome = JsonDocument.Parse(results[0]);
        var result = outcome.RootElement.GetProperty("result");
        Assert.Equal(("subscription", 5, 1), (result.GetProperty("kind").GetString(), result.GetProperty("eventsReceived").GetInt32(), result.GetProperty("reconnections").GetInt32()));
        Assert.Equal(["1", "2", "3", "4", "5"], outcome.RootElement.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("id").GetString()));
        Assert.Equal([1, 2, 3, 4, 5], outcome.RootElement.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("data").GetInt32()));
        // the second connection resumed after the last event the client received
        Assert.Equal(2, connections);
        Assert.Equal("", resumedFrom[0]);
        Assert.Equal("2", resumedFrom[1]);
    }

    [Fact]
    public async Task Without_the_declaration_a_dropped_stream_is_a_failure()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddTisilia(o => o.ApiId = "sse-no-resume");
        await using var app = builder.Build();
        var connections = 0;
        app.MapGet("/ticks", () => { Interlocked.Increment(ref connections); return TypedResults.ServerSentEvents(Ticks(0)); }).WithTisiliaOperation("ticks");
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        Assert.Null(Assert.IsType<SseResponseBody>(Assert.Single(export.Index!.Operations["ticks"].Responses).Body).Resume);
        await using var relay = new DroppingRelay(app.Urls.Single());
        var results = await InterpreterRequests.SendAsync(_dir, export.Text!, relay.Url, [("ticks", new { reconnect = new { maxAttempts = 3, delayMs = 10 } })], subscribe: true);
        using var outcome = JsonDocument.Parse(results[0]);
        Assert.Equal("transport-failure", outcome.RootElement.GetProperty("result").GetProperty("kind").GetString());
        Assert.Equal(2, outcome.RootElement.GetProperty("events").GetArrayLength());
        Assert.Equal(1, connections);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_declaration_on_a_controller_action_or_on_an_endpoint_without_events(bool withoutEvents)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddControllers().ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new OnlyController()));
        builder.Services.AddTisilia(o => o.ApiId = "sse-resume-mvc");
        await using var app = builder.Build();
        if (withoutEvents) { app.MapGet("/plain", () => 1).WithTisiliaEventResume().WithTisiliaOperation("plain"); }
        app.MapControllers();
        await app.StartAsync();
        var export = app.Services.GetRequiredService<TisiliaContractExporter>().ExportFresh();
        if (!withoutEvents)
        {
            Assert.False(export.Diagnostics.HasErrors, string.Join("\n", export.Diagnostics.Items));
            Assert.Equal("last-event-id", Assert.IsType<SseResponseBody>(Assert.Single(export.Index!.Operations["mvc-ticks"].Responses).Body).Resume);
            return;
        }
        // an endpoint that writes no events cannot declare that they resume
        var diagnostic = Assert.Single(export.Diagnostics.Items, d => d.Code == TisiliaCodes.MediaTypeInvalid);
        Assert.Contains("'plain'", diagnostic.Message, StringComparison.Ordinal);
        Assert.Null(export.Text);
    }

    private sealed class OnlyController : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            feature.Controllers.Clear(); feature.Controllers.Add(typeof(ResumingEventsController).GetTypeInfo());
        }
    }
}

[ApiController]
[NonController]
public sealed class ResumingEventsController : ControllerBase
{
    [HttpGet("/mvc-ticks")]
    [TisiliaOperation("mvc-ticks")]
    [TisiliaEventResume]
    public ServerSentEventsResult<int> Get() => TypedResults.ServerSentEvents(SseTests.Values(1, 2));
}
