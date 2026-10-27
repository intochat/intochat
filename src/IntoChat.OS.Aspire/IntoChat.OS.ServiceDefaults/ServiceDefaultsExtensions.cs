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

namespace IntoChat.OS.ServiceDefaults;

public static class ServiceDefaultsExtensions
{
    private const string TelemetryPrefix = "IntoChat.OS";
    private const string AzuriteAccountPathSegment = "/devstoreaccount1/";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        LegacyConfigurationGuard.Verify(builder.Configuration, builder.Environment);
        OpenPostureLoopback.Validate(builder.Configuration);
        BindPolicyKestrel.Validate(builder.Configuration);
        builder.Services.AddOpenPostureBackstop();
        // The process default keeps the log at Warning; the Open-posture listening URLs are still logged.
        builder.Logging.AddFilter(typeof(OpenPostureLoopback).FullName, LogLevel.Information);

        ConfigureOpenTelemetry(builder);
        AddDefaultHealthChecks(builder);
        builder.Services.AddServiceDiscovery();
        builder.Services.ConfigureHttpClientDefaults(http => http.AddServiceDiscovery());

        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapHealthProbes();
        app.MapTelemetryExportBarrier();
        return app;
    }

    private static void AddDefaultHealthChecks<TBuilder>(TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddSingleton<Drain>();
        builder.Services.AddHostedService(static services => services.GetRequiredService<Drain>());
        builder.Services
            .AddHealthChecks()
            .AddCheck("self", static () => HealthCheckResult.Healthy(), [HealthProbes.LiveTag])
            .AddCheck<Drain>(HealthProbes.DrainTag, tags: [HealthProbes.DrainTag]);
    }

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
        // Noise is dropped by name before the parent decides: a keepalive grain call runs inside the
        // sampled report request, so a root-only check would let it through as a child.
        var sampler = new SuppressNoiseSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(sampleRatio)));

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
            // A host that names itself (Aspire's OTEL_SERVICE_NAME and service.instance.id, a candidate's
            // candidate-{sha}) keeps that name, so its telemetry lands on its own resource; AddService would
            // overwrite both with the assembly name and a fresh instance id.
            .ConfigureResource(resource =>
            {
                // The configured name is used as is: the SDK's detector reads only the process environment, so a
                // name from another configuration source (appsettings, a command line) would otherwise be lost.
                if (builder.Configuration["OTEL_SERVICE_NAME"] is { Length: > 0 } configured && !string.IsNullOrWhiteSpace(configured))
                { resource.AddService(serviceName: configured, serviceNamespace: TelemetryPrefix, autoGenerateServiceInstanceId: false); }
                else { resource.AddService(serviceName: builder.Environment.ApplicationName, serviceNamespace: TelemetryPrefix); }
            })
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
                .SetSampler(sampler)
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
                        !context.Request.Path.Equals(TelemetryExportBarrier.Path, StringComparison.OrdinalIgnoreCase)
                        &&
                        !context.Request.Path.StartsWithSegments(HealthProbes.HealthPath, StringComparison.OrdinalIgnoreCase)
                        && !context.Request.Path.StartsWithSegments(HealthProbes.AlivePath, StringComparison.OrdinalIgnoreCase)
                        && !context.Request.Path.StartsWithSegments(HealthProbes.ReadyPath, StringComparison.OrdinalIgnoreCase))
                .AddHttpClientInstrumentation(options =>
                {
                    options.FilterHttpRequestMessage = static request => !IsAzuriteRequest(request)
                        && !string.Equals(request.RequestUri?.AbsolutePath, TelemetryExportBarrier.Path, StringComparison.OrdinalIgnoreCase);
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

    // RuntimeProtocol.ReportKindTag / HeartbeatReport (IntoChat.OS.Client.Runtime.V1), restated: ServiceDefaults
    // references neither the client nor the module.
    private const string ReportKindTag = "intochatos.runtime.report";
    private const string HeartbeatReport = "heartbeat";

    // The sandbox session heartbeat, every 20 s per brain session, tagged by the runtime edge endpoint. Other
    // reports on the same route (Subscribe, status, ...) keep their spans, and so does a refused or failed heartbeat.
    private static bool IsSuccessfulKeepaliveReport(Activity activity)
        => activity.Kind is ActivityKind.Server or ActivityKind.Client
            && activity.Status != ActivityStatusCode.Error
            && activity.GetTagItem("http.response.status_code") is int status and < 400
            && activity.GetTagItem(ReportKindTag) is HeartbeatReport;

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
