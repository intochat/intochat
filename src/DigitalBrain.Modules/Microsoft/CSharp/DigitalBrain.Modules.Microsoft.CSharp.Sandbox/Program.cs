using DigitalBrain.Client.Runtime.V1;
using DigitalBrain.Microsoft.CSharp.Sandbox;
using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.AddOptions<SandboxOptions>().BindConfiguration(SandboxOptions.SectionName);
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.PropertyNamingPolicy = RuntimeProtocol.Json.PropertyNamingPolicy);
builder.Services.AddHttpClient<BehaviorLoader>();
builder.Services.AddSingleton<Sessions>();
// The host reports its logs and spans wherever the deployment points OTLP (the Aspire dashboard locally).
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    builder.Logging.AddOpenTelemetry(logging => { logging.IncludeFormattedMessage = true; logging.IncludeScopes = true; });
    builder.Services.AddOpenTelemetry()
        .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddProcessor(new QuietReportProcessor()))
        .UseOtlpExporter();
}

var app = builder.Build();
app.Logger.LogInformation("Sandbox host carries contracts {Fingerprint}", Sessions.Fingerprint.Replace('\n', ' '));
app.MapSandbox();
await app.RunAsync();

// RuntimeProtocol.Report: the sandbox's own heartbeat POST. The route also carries Subscribe,
// so only a successful call is quiet; a refusal or error keeps its span.
internal sealed class QuietReportProcessor : BaseProcessor<Activity>
{
    public override void OnEnd(Activity activity)
    {
        if (activity.Kind == ActivityKind.Client
            && activity.Status != ActivityStatusCode.Error
            && activity.GetTagItem("http.response.status_code") is int status and < 400
            && activity.GetTagItem("url.full") is string url
            && url.EndsWith("/runtime/v1/report", StringComparison.Ordinal))
        {
            activity.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
            activity.IsAllDataRequested = false;
        }
    }
}
