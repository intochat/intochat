using IntoChat.OS.AI;
using IntoChat.OS.AI.Models;

namespace IntoChat.OS.Kernel.Tests.E2E;

// Boots the product with the real candidate and cluster hosts; the fact leases it.
public sealed class SelfImproveHostFixture : ReferenceBrainFixture
{
    protected override Task<ITestBrain> StartBrainAsync(CancellationToken cancellationToken)
        => ProductE2E.Create()
            .WithOptions<AIModule, AIOptions>(ai => ai.WithModelEndpoint(AiProvider.OpenAI, Model.Endpoint))
            .WithStartupTimeout(TimeSpan.FromMinutes(15))
            .WithDashboardTelemetry()
            .StartAsync(cancellationToken);
}
