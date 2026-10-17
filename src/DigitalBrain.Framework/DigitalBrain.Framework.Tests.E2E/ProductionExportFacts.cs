using DigitalBrain.Testing.Aspire.Diagnostics;

namespace DigitalBrain.Framework.Tests.E2E;

// A Production host must export like a Development one: the capture facts run Production to
// catch environment gates, and the gate they found on 2026-10-07 was in the exporter itself.
[Collection("ProductionBrainFixture")]
public sealed class ProductionExportFacts(ProductionBrainFixture host)
{
    [Fact(Timeout = 300_000)]
    public async Task AProductionHostExportsGrainCallSpans()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await host.LeaseAsync(ct);
        using var operation = brain.Telemetry().BeginOperation(nameof(AProductionHostExportsGrainCallSpans));
        using var request = operation.Request(HttpMethod.Get, "/ai/models");
        using var response = await operation.SendAsync(brain.HttpClient, request, ct);
        Assert.True(response.IsSuccessStatusCode);
        var capture = await operation.CompleteAsync(ct);
        Assert.Contains(capture.Spans, span => span.Scope == "Microsoft.Orleans.Application");
    }
}
