using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using IntoChat.OS.ServiceDefaults;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace IntoChat.OS.Aspire.Hosting.Tests.Unit;

public sealed class TelemetryExportBarrierAdversarialFacts
{
    private sealed class FlushProcessor<T>(bool success, Action? onFlush = null) : BaseProcessor<T> where T : class
    {
        public bool Flushed { get; private set; }
        protected override bool OnForceFlush(int timeoutMilliseconds)
        {
            Flushed = true;
            onFlush?.Invoke();
            return success;
        }
    }

    private sealed class FlushMetricExporter(bool success, Action? onFlush = null) : BaseExporter<Metric>
    {
        public bool Flushed { get; private set; }
        public bool ExportCalled { get; private set; }
        public override ExportResult Export(in Batch<Metric> batch)
        {
            ExportCalled = true;
            onFlush?.Invoke();
            return success ? ExportResult.Success : ExportResult.Failure;
        }
        protected override bool OnForceFlush(int timeoutMilliseconds)
        {
            Flushed = true;
            onFlush?.Invoke();
            return success;
        }
    }

    [Fact]
    public async Task AllThreeSignalsMustFlushBeforeReturningReceipt()
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [TelemetryExportBarrier.CapabilityEnvironmentVariable] = "adversarial-secret-token-12345"
        });

        var traces = new FlushProcessor<Activity>(true);
        var metrics = new FlushMetricExporter(true);
        var logs = new FlushProcessor<LogRecord>(true);

        builder.Services.AddOpenTelemetry()
            .WithTracing(tracing => tracing.AddSource(TelemetryExportBarrier.SourceName).AddProcessor(traces))
            .WithMetrics(metering => metering.AddReader(new BaseExportingMetricReader(metrics)));
        builder.Logging.AddOpenTelemetry(logging => logging.AddProcessor(logs));

        await using var app = builder.Build();
        app.MapTelemetryExportBarrier();
        await app.StartAsync(ct);

        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        http.DefaultRequestHeaders.Add(TelemetryExportBarrier.CapabilityHeader, "adversarial-secret-token-12345");

        using var response = await http.PostAsync(TelemetryExportBarrier.Path, null, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(traces.Flushed, "Traces should be flushed");
        Assert.True(metrics.ExportCalled, "Metrics should be exported/flushed");
        Assert.True(logs.Flushed, "Logs should be flushed");

        var receipt = await response.Content.ReadFromJsonAsync<TelemetryExportReceipt>(ct);
        Assert.NotNull(receipt);
        Assert.NotEqual(Guid.Empty, receipt.Marker);
        Assert.True(receipt.CompletedAt <= DateTimeOffset.UtcNow);

        await app.StopAsync(ct);
    }

    [Fact]
    public async Task MetricsOptional_WhenMeterProviderNotRegistered_TracesAndLogsStillFlush()
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [TelemetryExportBarrier.CapabilityEnvironmentVariable] = "test-secret"
        });

        var traces = new FlushProcessor<Activity>(true);
        var logs = new FlushProcessor<LogRecord>(true);

        builder.Services.AddOpenTelemetry()
            .WithTracing(tracing => tracing.AddSource(TelemetryExportBarrier.SourceName).AddProcessor(traces));
        builder.Logging.AddOpenTelemetry(logging => logging.AddProcessor(logs));

        await using var app = builder.Build();
        app.MapTelemetryExportBarrier();
        await app.StartAsync(ct);

        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        http.DefaultRequestHeaders.Add(TelemetryExportBarrier.CapabilityHeader, "test-secret");

        using var response = await http.PostAsync(TelemetryExportBarrier.Path, null, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(traces.Flushed);
        Assert.True(logs.Flushed);

        await app.StopAsync(ct);
    }

    [Theory]
    [InlineData(true, true, false, HttpStatusCode.ServiceUnavailable)] // Log fails
    [InlineData(true, false, true, HttpStatusCode.ServiceUnavailable)] // Meter fails
    [InlineData(false, true, true, HttpStatusCode.ServiceUnavailable)] // Trace fails
    [InlineData(false, false, false, HttpStatusCode.ServiceUnavailable)] // All fail
    public async Task TripleSignalFailClosed_WhenAnySignalFailsToFlush(
        bool traceSuccess, bool meterSuccess, bool logSuccess, HttpStatusCode expected)
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [TelemetryExportBarrier.CapabilityEnvironmentVariable] = "test-barrier-secret"
        });

        var traces = new FlushProcessor<Activity>(traceSuccess);
        var metrics = new FlushMetricExporter(meterSuccess);
        var logs = new FlushProcessor<LogRecord>(logSuccess);

        builder.Services.AddOpenTelemetry()
            .WithTracing(tracing => tracing.AddSource(TelemetryExportBarrier.SourceName).AddProcessor(traces))
            .WithMetrics(metering => metering.AddReader(new BaseExportingMetricReader(metrics)));
        builder.Logging.AddOpenTelemetry(logging => logging.AddProcessor(logs));

        await using var app = builder.Build();
        app.MapTelemetryExportBarrier();
        await app.StartAsync(ct);

        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        http.DefaultRequestHeaders.Add(TelemetryExportBarrier.CapabilityHeader, "test-barrier-secret");

        using var response = await http.PostAsync(TelemetryExportBarrier.Path, null, ct);

        Assert.Equal(expected, response.StatusCode);
        await app.StopAsync(ct);
    }

    [Fact]
    public async Task MultipleLoggerProviders_SecondFails_FailClosedReturns503()
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [TelemetryExportBarrier.CapabilityEnvironmentVariable] = "test-barrier-secret"
        });

        var traces = new FlushProcessor<Activity>(true);
        var logs1 = new FlushProcessor<LogRecord>(true);
        var logs2 = new FlushProcessor<LogRecord>(false); // second logger fails

        builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing.AddSource(TelemetryExportBarrier.SourceName).AddProcessor(traces));
        builder.Logging.AddOpenTelemetry(logging => logging.AddProcessor(logs1));
        builder.Logging.AddOpenTelemetry(logging => logging.AddProcessor(logs2));

        await using var app = builder.Build();
        app.MapTelemetryExportBarrier();
        await app.StartAsync(ct);

        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        http.DefaultRequestHeaders.Add(TelemetryExportBarrier.CapabilityHeader, "test-barrier-secret");

        using var response = await http.PostAsync(TelemetryExportBarrier.Path, null, ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        await app.StopAsync(ct);
    }

    [Theory]
    [InlineData("")] // Empty header
    [InlineData("wrong")] // Short wrong
    [InlineData("test-barrier-secreT")] // Single character difference (case)
    [InlineData("test-barrier-secre")] // Off by one (missing last char)
    [InlineData("test-barrier-secret-extended")] // Off by one (extra char)
    [InlineData("TEST-BARRIER-SECRET")] // Upper case
    [InlineData("!@#$%^&*()_+-=[]{}|;':,./<>?")] // Special characters
    public async Task AuthenticationFailsWith401_OnAnyMismatchedHeader(string suppliedHeader)
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [TelemetryExportBarrier.CapabilityEnvironmentVariable] = "test-barrier-secret"
        });

        var traces = new FlushProcessor<Activity>(true);
        var logs = new FlushProcessor<LogRecord>(true);
        builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing.AddSource(TelemetryExportBarrier.SourceName).AddProcessor(traces));
        builder.Logging.AddOpenTelemetry(logging => logging.AddProcessor(logs));

        await using var app = builder.Build();
        app.MapTelemetryExportBarrier();
        await app.StartAsync(ct);

        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var request = new HttpRequestMessage(HttpMethod.Post, TelemetryExportBarrier.Path);
        request.Headers.TryAddWithoutValidation(TelemetryExportBarrier.CapabilityHeader, suppliedHeader);

        using var response = await http.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(traces.Flushed, "Flushing must not occur when authentication fails");

        await app.StopAsync(ct);
    }

    [Fact]
    public async Task EmbeddedNullByteInHeader_RejectedByServerWithBadRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [TelemetryExportBarrier.CapabilityEnvironmentVariable] = "test-barrier-secret"
        });

        await using var app = builder.Build();
        app.MapTelemetryExportBarrier();
        await app.StartAsync(ct);

        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var request = new HttpRequestMessage(HttpMethod.Post, TelemetryExportBarrier.Path);
        request.Headers.TryAddWithoutValidation(TelemetryExportBarrier.CapabilityHeader, "test-barrier-secret\0nullbyte");

        using var response = await http.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await app.StopAsync(ct);
    }

    [Fact]
    public async Task AuthenticationFailsWith401_WhenDuplicateHeadersAreSupplied()
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [TelemetryExportBarrier.CapabilityEnvironmentVariable] = "test-barrier-secret"
        });

        var traces = new FlushProcessor<Activity>(true);
        var logs = new FlushProcessor<LogRecord>(true);
        builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing.AddSource(TelemetryExportBarrier.SourceName).AddProcessor(traces));
        builder.Logging.AddOpenTelemetry(logging => logging.AddProcessor(logs));

        await using var app = builder.Build();
        app.MapTelemetryExportBarrier();
        await app.StartAsync(ct);

        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var request = new HttpRequestMessage(HttpMethod.Post, TelemetryExportBarrier.Path);
        request.Headers.Add(TelemetryExportBarrier.CapabilityHeader, ["test-barrier-secret", "test-barrier-secret"]);

        using var response = await http.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(traces.Flushed);

        await app.StopAsync(ct);
    }

    [Fact]
    public async Task ConstantTimeAuthentication_HandlesLargeHeadersGracefully()
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [TelemetryExportBarrier.CapabilityEnvironmentVariable] = "test-barrier-secret"
        });

        var traces = new FlushProcessor<Activity>(true);
        var logs = new FlushProcessor<LogRecord>(true);
        builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing.AddSource(TelemetryExportBarrier.SourceName).AddProcessor(traces));
        builder.Logging.AddOpenTelemetry(logging => logging.AddProcessor(logs));

        await using var app = builder.Build();
        app.MapTelemetryExportBarrier();
        await app.StartAsync(ct);

        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var request = new HttpRequestMessage(HttpMethod.Post, TelemetryExportBarrier.Path);
        // Supply a 4KB adversarial string (within Kestrel header buffer, testing SHA-256 and constant-time check)
        var largeBogusToken = new string('A', 4096);
        request.Headers.Add(TelemetryExportBarrier.CapabilityHeader, largeBogusToken);

        using var response = await http.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(traces.Flushed);

        await app.StopAsync(ct);
    }

    [Fact]
    public async Task FlushThrowsException_Returns503WithoutLeakingDetails()
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [TelemetryExportBarrier.CapabilityEnvironmentVariable] = "test-barrier-secret"
        });

        var traces = new FlushProcessor<Activity>(false, onFlush: () => throw new InvalidOperationException("Simulated catastrophic crash in OTel exporter"));
        var logs = new FlushProcessor<LogRecord>(true);
        builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing.AddSource(TelemetryExportBarrier.SourceName).AddProcessor(traces));
        builder.Logging.AddOpenTelemetry(logging => logging.AddProcessor(logs));

        await using var app = builder.Build();
        app.MapTelemetryExportBarrier();
        await app.StartAsync(ct);

        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        http.DefaultRequestHeaders.Add(TelemetryExportBarrier.CapabilityHeader, "test-barrier-secret");

        using var response = await http.PostAsync(TelemetryExportBarrier.Path, null, ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.DoesNotContain("Simulated catastrophic crash", body);

        await app.StopAsync(ct);
    }

    [Fact]
    public async Task MeterFlushThrowsException_Returns503WithoutLeakingDetails()
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [TelemetryExportBarrier.CapabilityEnvironmentVariable] = "test-barrier-secret"
        });

        var traces = new FlushProcessor<Activity>(true);
        var metrics = new FlushMetricExporter(false, onFlush: () => throw new NotSupportedException("Simulated meter crash"));
        var logs = new FlushProcessor<LogRecord>(true);

        builder.Services.AddOpenTelemetry()
            .WithTracing(tracing => tracing.AddSource(TelemetryExportBarrier.SourceName).AddProcessor(traces))
            .WithMetrics(metering => metering.AddReader(new BaseExportingMetricReader(metrics)));
        builder.Logging.AddOpenTelemetry(logging => logging.AddProcessor(logs));

        await using var app = builder.Build();
        app.MapTelemetryExportBarrier();
        await app.StartAsync(ct);

        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        http.DefaultRequestHeaders.Add(TelemetryExportBarrier.CapabilityHeader, "test-barrier-secret");

        using var response = await http.PostAsync(TelemetryExportBarrier.Path, null, ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.DoesNotContain("Simulated meter crash", body);

        await app.StopAsync(ct);
    }
}
