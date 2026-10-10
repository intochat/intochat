extern alias AppHost;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;

namespace DigitalBrain.OS.Tests.Unit;

// Telemetry is Aspire's: the AppHost's dashboard OTLP setting is what every project resource
// exports to. A test collector plugs in there, never by writing OTEL_* onto a resource.
[Collection(AppHostComposition.Collection)]
public sealed class TelemetryProjectionFacts
{
    [Fact]
    public async Task TheOsExportsOverHttpProtobufToTheAppHostsHttpOtlpEndpoint()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var appHost = await DistributedApplicationTestingBuilder.CreateAsync<AppHost::Projects.DigitalBrain_AppHost>(
            ["Parameters:Modules-digitalbrain-master-key=synthetic-composition-test-key-32chars",
             "ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL=http://127.0.0.1:4999",
             "ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL="], ct);
        var runtime = appHost.Resources.Single(resource => resource.Name == "OperatingSystem-a");
        var environment = new Dictionary<string, object>();
        var context = new EnvironmentCallbackContext(appHost.ExecutionContext, runtime, environment, ct);
        foreach (var callback in runtime.Annotations.OfType<EnvironmentCallbackAnnotation>()) { await callback.Callback(context); }
        Assert.Equal("http/protobuf", environment["OTEL_EXPORTER_OTLP_PROTOCOL"]?.ToString());
        Assert.Contains("127.0.0.1:4999", environment["OTEL_EXPORTER_OTLP_ENDPOINT"]?.ToString());
    }
}
