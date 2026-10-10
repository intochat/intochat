using System.Diagnostics;
using DigitalBrain.Client.Runtime.V1;
using DigitalBrain.Microsoft.CSharp.Sandbox;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.AddOptions<SandboxOptions>().BindConfiguration(SandboxOptions.SectionName);
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.PropertyNamingPolicy = RuntimeProtocol.Json.PropertyNamingPolicy);
builder.Services.AddHttpClient<BehaviorLoader>();
builder.Services.AddSingleton<Sessions>();
builder.Services.AddSingleton<RuleJudge>();
// The host reports its logs and spans wherever the deployment points OTLP (the Aspire dashboard locally).
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    builder.Logging.AddOpenTelemetry(logging => { logging.IncludeFormattedMessage = true; logging.IncludeScopes = true; });
    builder.Services.AddOpenTelemetry()
        .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation(http => http.EnrichWithHttpRequestMessage = (activity, request) =>
        {
            if (request.Options.TryGetValue(RuntimeProtocol.ReportKind, out var kind)) { activity.SetTag(RuntimeProtocol.ReportKindTag, kind); }
        }).AddProcessor(new QuietReportProcessor()))
        .UseOtlpExporter();
}

var app = builder.Build();
app.Logger.LogInformation("Sandbox host carries contracts {Fingerprint}", Sessions.Fingerprint.Replace('\n', ' '));
app.MapSandbox();
await app.RunAsync();

// The sandbox's own heartbeat POST, tagged by SessionConnection. Other reports on the route (Subscribe, status, ...)
// keep their spans, and so does a heartbeat the brain refused or that failed.
internal sealed class QuietReportProcessor : BaseProcessor<Activity>
{
    public override void OnEnd(Activity activity)
    {
        if (activity.Kind == ActivityKind.Client
            && activity.Status != ActivityStatusCode.Error
            && activity.GetTagItem("http.response.status_code") is int status and < 400
            && activity.GetTagItem(RuntimeProtocol.ReportKindTag) is RuntimeProtocol.HeartbeatReport)
        {
            activity.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
            activity.IsAllDataRequested = false;
        }
    }
}
