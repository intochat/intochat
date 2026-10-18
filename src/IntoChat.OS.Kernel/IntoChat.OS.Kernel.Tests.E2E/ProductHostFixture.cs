using IntoChat.OS;
using IntoChat.OS.AI;
using IntoChat.OS.AI.Models;
using IntoChat.OS.Apps;
using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Aspire.Hosting;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Configuration;

namespace IntoChat.OS.Kernel.Tests.E2E;

// Product-only shipped content; all lease, model, browser and telemetry support is shared.
public sealed class ProductHostFixture : ReferenceBrainFixture
{
    public static readonly string[] ShippedPackages = ["os/settings", "os/ino", "os/router", "os/calculator"];

    protected override Task<ITestBrain> StartBrainAsync(CancellationToken cancellationToken)
        => ProductE2E.Create()
            .WithOptions<AIModule, AIOptions>(ai => ai.WithModelEndpoint(AiProvider.OpenAI, Model.Endpoint))
            .WithOptions<FlutterModule, FlutterModuleOptions>(flutter => flutter.BackendOnly())
            .WithDashboardTelemetry()
            .StartAsync(cancellationToken);
}
