namespace DigitalBrain.OS.Tests.E2E;

// Boots the product with the real candidate and cluster hosts; the fact leases it.
public sealed class SelfImproveHostFixture : ReferenceBrainFixture
{
    protected override Task<ITestBrain> StartBrainAsync(CancellationToken cancellationToken)
        => ProductE2E.Create()
            .WithStartupTimeout(TimeSpan.FromMinutes(15))
            .ExportingTelemetryTo(Collector)
            .StartAsync(cancellationToken);
}
