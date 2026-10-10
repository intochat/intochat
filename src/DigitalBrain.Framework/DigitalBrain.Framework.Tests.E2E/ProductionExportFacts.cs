using DigitalBrain.Testing.E2E;
using DigitalBrain.Testing.Aspire.Diagnostics;
using static DigitalBrain.Testing.Aspire.Diagnostics.TraceAssertions;

namespace DigitalBrain.Framework.Tests.E2E;

// A Production host must export like a Development one: the capture facts run Production to
// catch environment gates, and the gate they found on 2026-10-07 was in the exporter itself.
public sealed class ProductionExportFacts(ProductionBrainFixture host)
{
    [Fact(Timeout = 300_000)]
    public async Task AProductionHostExportsGrainCallSpans()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await host.LeaseAsync(ct);
        using var response = await brain.HttpClient.GetAsync("/ai/models", ct);
        Assert.True(response.IsSuccessStatusCode);
        await WaitForAsync(() => host.Collector.Snapshot().Any(span => span.Scope == "Microsoft.Orleans.Application"), ct);
    }
}
