using System.Net;
using System.Diagnostics;
using System.Net.Http.Json;
using IntoChat.OS.ServiceDefaults;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;

namespace IntoChat.OS.Aspire.Hosting.Tests.Unit;

public sealed class TelemetryExportBarrierFacts
{
    [Theory]
    [InlineData(true, true, HttpStatusCode.OK)]
    [InlineData(false, true, HttpStatusCode.ServiceUnavailable)]
    [InlineData(true, false, HttpStatusCode.ServiceUnavailable)]
    public async Task BothSignalsMustFlushBeforeReturningAReceipt(bool traceSuccess, bool logSuccess, HttpStatusCode expected)
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        { [TelemetryExportBarrier.CapabilityEnvironmentVariable] = "capability" });
        var traces = new FlushProcessor<Activity>(traceSuccess);
        var logs = new FlushProcessor<LogRecord>(logSuccess);
        builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing.AddSource(TelemetryExportBarrier.SourceName).AddProcessor(traces));
        builder.Logging.AddOpenTelemetry(logging => logging.AddProcessor(logs));
        await using var app = builder.Build();
        app.MapTelemetryExportBarrier();
        await app.StartAsync(ct);
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        http.DefaultRequestHeaders.Add(TelemetryExportBarrier.CapabilityHeader, "capability");
        using var response = await http.PostAsync(TelemetryExportBarrier.Path, null, ct);
        Assert.Equal(expected, response.StatusCode);
        Assert.True(traces.Flushed);
        if (traceSuccess) { Assert.True(logs.Flushed); }
        if (expected == HttpStatusCode.OK)
        {
            var receipt = await response.Content.ReadFromJsonAsync<TelemetryExportReceipt>(ct);
            Assert.NotNull(receipt);
            Assert.NotEqual(Guid.Empty, receipt.Marker);
        }
        await app.StopAsync(ct);
    }

    private sealed class FlushProcessor<T>(bool success) : BaseProcessor<T> where T : class
    {
        public bool Flushed { get; private set; }
        protected override bool OnForceFlush(int timeoutMilliseconds) { Flushed = true; return success; }
    }

    [Theory]
    [InlineData(null, null, HttpStatusCode.NotFound)]
    [InlineData("capability", null, HttpStatusCode.Unauthorized)]
    [InlineData("capability", "wrong", HttpStatusCode.Unauthorized)]
    [InlineData("capability", "capability", HttpStatusCode.ServiceUnavailable)]
    public async Task MissingCapabilityOrProvidersNeverReportsACompletedExport(string? configured, string? supplied, HttpStatusCode expected)
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        { [TelemetryExportBarrier.CapabilityEnvironmentVariable] = configured });
        await using var app = builder.Build();
        app.MapTelemetryExportBarrier();
        await app.StartAsync(ct);
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var request = new HttpRequestMessage(HttpMethod.Post, TelemetryExportBarrier.Path);
        if (supplied is not null) { request.Headers.Add(TelemetryExportBarrier.CapabilityHeader, supplied); }
        using var response = await http.SendAsync(request, ct);
        Assert.Equal(expected, response.StatusCode);
        await app.StopAsync(ct);
    }
}
