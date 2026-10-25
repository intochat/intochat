using IntoChat.OS;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Connections.Configuration;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Framework.Modules.Configuration;

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
