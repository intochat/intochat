using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;

namespace DigitalBrain.Testing;

public static class InMemoryTelemetry
{
    // Adds OpenTelemetry in-memory exporters for traces and logs next to the host's own pipeline, so telemetry facts read
    // what the brain emitted without a dashboard. Spans come from the sources the service defaults already subscribe.
    public static TestBrain WithInMemoryTelemetry(this TestBrain brain, out InMemoryTelemetrySink sink)
    {
        ArgumentNullException.ThrowIfNull(brain);
        var captured = new InMemoryTelemetrySink();
        sink = captured;
        return brain.ConfigureServices(services => services.AddOpenTelemetry()
            .WithTracing(tracing => tracing.AddInMemoryExporter(captured.Activities))
            .WithLogging(logging => logging.AddInMemoryExporter(captured.Logs)));
    }
}
