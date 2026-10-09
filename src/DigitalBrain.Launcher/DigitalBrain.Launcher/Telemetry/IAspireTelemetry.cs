namespace DigitalBrain.Launcher.Telemetry;

public interface IAspireTelemetry
{
    Task<int> CountSpans(string appHostPath, string name, CancellationToken ct);
}