using DigitalBrain.Revisions;

namespace DigitalBrain.OS.Tests.E2E;

// Boots the product with the fake launcher named as the revisions launcher; the fact leases it.
public sealed class SelfImproveHostFixture : ReferenceBrainFixture
{
    public FakeLauncherHost Launcher { get; private set; } = null!;

    protected override async Task<E2EBrain> StartBrainAsync(CancellationToken cancellationToken)
    {
        // The product's MCP port is fixed (AppHost.cs: DigitalBrain:Http:McpPort, 5081); the fake launcher names it as the child.
        Launcher = await FakeLauncherHost.StartAsync("http://localhost:5081", cancellationToken);
        return await ProductE2E.Create()
            .ConfigureModule<RevisionsModule, RevisionsOptions>(revisions => revisions.LauncherUrl = Launcher.Url)
            .WithStartupTimeout(TimeSpan.FromMinutes(15))
            .ExportingTelemetryTo(Collector)
            .StartAsync(cancellationToken);
    }
}