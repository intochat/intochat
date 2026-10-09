using DigitalBrain.Launcher.Telemetry;

namespace DigitalBrain.Launcher.Tests.Unit;

internal sealed class FakeAspireTelemetry : IAspireTelemetry
{
    public Dictionary<(string AppHost, string Span), int> Counts { get; } = [];
    public Task<int> CountSpans(string appHostPath, string name, CancellationToken ct) => Task.FromResult(Counts.GetValueOrDefault((appHostPath, name)));
}