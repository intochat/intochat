using System.Diagnostics;
using DigitalBrain.Testing.Neurons;

namespace DigitalBrain.Testing.Tests.Integration;

public sealed class InMemoryTelemetryFacts
{
    private const string Source = "DigitalBrain.Testing.InMemoryTelemetryFacts";

    [Trait("host", "own")]
    [Fact]
    public async Task ReadersSnapshotTheSinkWhileTheHostKeepsExporting()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<PulseModule>().WithInMemoryTelemetry(out var sink, Source).StartAsync(ct);
        using var source = new ActivitySource(Source);

        // Spans finish on many threads while a reader enumerates snapshots; an unlocked list throws on the concurrent append.
        var exporting = Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var span = 0; span < 50; span++) { using var activity = source.StartActivity($"span-{worker}-{span}"); }
        }, ct)));
        while (!exporting.IsCompleted) { _ = sink.Spans().Count(span => span.Source.Name == Source); }
        await exporting;

        Assert.Equal(400, sink.Spans().Count(span => span.Source.Name == Source));
    }
}
