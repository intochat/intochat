using System.Diagnostics;
using OpenTelemetry.Logs;

namespace DigitalBrain.Testing;

// What the brain exported in process: every finished span and every log record, in export order.
// The exporters append from the SDK's export thread; read after the work under test has completed.
public sealed class InMemoryTelemetrySink
{
    public List<Activity> Activities { get; } = [];
    public List<LogRecord> Logs { get; } = [];
}
