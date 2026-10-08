using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace DigitalBrain.ServiceDefaults;

public static class ServiceDefaultsExtensions
{
    private const string AlivePath = "/alive";
    private const string HealthPath = "/health";
    private const string TelemetryPrefix = "DigitalBrain";
    private const string AzuriteAccountPathSegment = "/devstoreaccount1/";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        ConfigureOpenTelemetry(builder);
        AddDefaultHealthChecks(builder);
        builder.Services.AddServiceDiscovery();
        builder.Services.ConfigureHttpClientDefaults(http => http.AddServiceDiscovery());

        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapHealthChecks(HealthPath).AllowAnonymous();
        app.MapHealthChecks(AlivePath, new HealthCheckOptions
        {
            Predicate = static registration => registration.Tags.Contains("live"),
        }).AllowAnonymous();

        return app;
    }

    private static void AddDefaultHealthChecks<TBuilder>(TBuilder builder)
        where TBuilder : IHostApplicationBuilder
        => builder.Services
            .AddHealthChecks()
            .AddCheck("self", static () => HealthCheckResult.Healthy(), ["live"]);

    private static void AddOpenTelemetryExporters<TBuilder>(TBuilder builder, TelemetryIntegrationOptions integration)
        where TBuilder : IHostApplicationBuilder
    {
        if (!string.IsNullOrWhiteSpace(integration.ExporterEndpoint))
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }
    }

    private static void ConfigureOpenTelemetry<TBuilder>(TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        var options = builder.Configuration.GetSection(ServiceTelemetryOptions.SectionName).Get<ServiceTelemetryOptions>() ?? new();
        var integration = builder.Configuration.Get<TelemetryIntegrationOptions>() ?? new();
        var configuredSampleRatio = options.SampleRatio;
        var sampleRatio = Math.Clamp(configuredSampleRatio ?? 1d, 0d, 1d);
        var rootSampler = new SuppressNoiseSampler(new TraceIdRatioBasedSampler(sampleRatio));

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });
        // Framework chatter does not belong in traces or the log exporter: keep the collection
        // quiet at Warning while the GenAI categories opt back in below when capture is enabled.
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        if (integration.EnableSensitiveData == true)
        {
            // MEAI's structured inference events use this category. Broad Microsoft
            // Warning filters must not discard opted-in GenAI evidence. Only the explicit
            // module setting raises this category; the OTEL standard capture variable cannot
            // silently turn content logging on.
            builder.Logging.AddFilter("Microsoft.Extensions.AI.OpenTelemetryChatClient", LogLevel.Information);
        }

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                serviceName: builder.Environment.ApplicationName,
                serviceNamespace: TelemetryPrefix))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(TelemetryPrefix)
                .AddMeter($"{TelemetryPrefix}.*")
                .AddMeter("Experimental.Microsoft.Extensions.AI")
                .AddMeter("Experimental.Microsoft.Extensions.AI.*")
                .AddMeter("Microsoft.Extensions.AI")
                .AddMeter("Microsoft.Extensions.AI.*")
                .AddMeter("Microsoft.Orleans"))
            .WithTracing(tracing => tracing
                .SetSampler(new ParentBasedSampler(rootSampler))
                .AddProcessor(new SuppressNoiseActivityProcessor())
                .AddSource(builder.Environment.ApplicationName)
                .AddSource(TelemetryPrefix)
                .AddSource($"{TelemetryPrefix}.*")
                .AddSource("Experimental.Microsoft.Extensions.AI")
                .AddSource("Experimental.Microsoft.Extensions.AI.*")
                .AddSource("Microsoft.Extensions.AI")
                .AddSource("Microsoft.Extensions.AI.*")
                .AddSource("Microsoft.Orleans.Application")
                .AddSource("ClickHouse.Driver")
                .AddSource("Npgsql")
                .AddSource("Experimental.ModelContextProtocol")
                .AddAspNetCoreInstrumentation(options =>
                    options.Filter = context =>
                        !context.Request.Path.StartsWithSegments(HealthPath, StringComparison.OrdinalIgnoreCase)
                        && !context.Request.Path.StartsWithSegments(AlivePath, StringComparison.OrdinalIgnoreCase))
                .AddHttpClientInstrumentation(options =>
                {
                    options.FilterHttpRequestMessage = static request => !IsAzuriteRequest(request);
                }));

        AddOpenTelemetryExporters(builder, integration);
    }

    private static bool IsAzuriteRequest(HttpRequestMessage request)
    {
        var path = request.RequestUri?.AbsolutePath;
        return path is not null
            && path.Contains(AzuriteAccountPathSegment, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAzureStorageNoiseName(string name)
        => name.StartsWith("TableClient.", StringComparison.Ordinal)
            || name.StartsWith("TableServiceClient.", StringComparison.Ordinal)
            || name.StartsWith("BlobClient.", StringComparison.Ordinal)
            || name.StartsWith("BlobBaseClient.", StringComparison.Ordinal)
            || name.StartsWith("BlobContainerClient.", StringComparison.Ordinal)
            || name.StartsWith("BlobServiceClient.", StringComparison.Ordinal)
            || name.StartsWith("QueueClient.", StringComparison.Ordinal)
            || name.StartsWith("QueueServiceClient.", StringComparison.Ordinal);

    // Keepalive traffic is not a trace anyone reads: the sandbox session heartbeat, the authorize
    // call it rides on, and neuron watch renewals. Dropping them keeps an idle brain quiet.
    private static readonly HashSet<string> KeepaliveSpanNames = new(StringComparer.Ordinal)
    {
        "IBrainRuntimeEdge/Heartbeat",
        "IBrainRuntimeEdge/Authorize",
        "INeuron/Watch",
        "INeuron/Unwatch",
    };

    private static bool IsKeepaliveName(string name) => KeepaliveSpanNames.Contains(name);

    private static bool IsAzureStorageNoiseSource(string sourceName)
        => sourceName.StartsWith("Azure.Data.Tables", StringComparison.Ordinal)
            || sourceName.StartsWith("Azure.Storage", StringComparison.Ordinal)
            || string.Equals(sourceName, "Azure.Core.Http", StringComparison.Ordinal);

    private static bool IsAzureStorageNamespace(string? azNamespace)
        => azNamespace is "Microsoft.Tables"
            or "Microsoft.Storage"
            or "Microsoft.Blobs"
            or "Microsoft.Queue";

    private static bool IsAzureStorageNoise(Activity activity)
    {
        if (IsAzureStorageNoiseName(activity.OperationName)
            || IsAzureStorageNoiseSource(activity.Source.Name))
        {
            return true;
        }

        return activity.GetTagItem("az.namespace") is string azNamespace
            && IsAzureStorageNamespace(azNamespace);
    }

    // RuntimeProtocol.Report: the sandbox session heartbeat POST, every 20 s per brain session. The route also
    // carries Subscribe, so only a successful call is quiet; a refusal or error keeps its span.
    private static bool IsSuccessfulKeepaliveReport(Activity activity)
    {
        if (activity.Kind is not (ActivityKind.Server or ActivityKind.Client)
            || activity.Status == ActivityStatusCode.Error
            || activity.GetTagItem("http.response.status_code") is not int status
            || status >= 400)
        {
            return false;
        }

        var path = activity.GetTagItem("url.path") as string ?? activity.GetTagItem("url.full") as string;
        return path is not null && path.EndsWith("/runtime/v1/report", StringComparison.Ordinal);
    }

    private sealed class SuppressNoiseSampler(Sampler inner) : Sampler
    {
        public override SamplingResult ShouldSample(in SamplingParameters samplingParameters)
        {
            if (IsAzureStorageNoiseName(samplingParameters.Name) || IsKeepaliveName(samplingParameters.Name))
            {
                return new SamplingResult(SamplingDecision.Drop);
            }

            return inner.ShouldSample(in samplingParameters);
        }
    }

    private sealed class SuppressNoiseActivityProcessor : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity activity)
        {
            if (!IsAzureStorageNoise(activity) && !IsSuccessfulKeepaliveReport(activity))
            {
                return;
            }

            activity.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
            activity.IsAllDataRequested = false;
        }
    }
}
