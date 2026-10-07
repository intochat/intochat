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
// The host reports its logs and spans wherever the deployment points OTLP (the Aspire dashboard locally).
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    builder.Logging.AddOpenTelemetry(logging => { logging.IncludeFormattedMessage = true; logging.IncludeScopes = true; });
    builder.Services.AddOpenTelemetry()
        .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation())
        .UseOtlpExporter();
}

var app = builder.Build();
app.Logger.LogInformation("Sandbox host carries contracts {Fingerprint}", Sessions.Fingerprint.Replace('\n', ' '));
app.MapSandbox();
await app.RunAsync();
