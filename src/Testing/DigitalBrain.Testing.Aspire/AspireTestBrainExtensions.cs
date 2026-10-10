using DigitalBrain.Aspire.Hosting;
using DigitalBrain.Testing.Aspire.Diagnostics;

namespace DigitalBrain.Testing.Aspire;

public static class AspireTestBrainExtensions
{
    // The collector plugs into the AppHost's own OTLP setting; Aspire projects it to every resource.
    public static TestBrain ExportingTelemetryTo(this TestBrain brain, TestTelemetryCollector collector)
    {
        ArgumentNullException.ThrowIfNull(brain);
        ArgumentNullException.ThrowIfNull(collector);
        return brain.WithHostConfiguration("ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL", collector.Endpoint.AbsoluteUri)
            .WithHostConfiguration("ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL", "");
    }

    public static TestBrain InEnvironment(this TestBrain brain, string environmentName)
    {
        ArgumentNullException.ThrowIfNull(brain);
        return brain.WithHostConfiguration(DigitalBrainHostingOptions.EnvironmentKey, environmentName);
    }
}
