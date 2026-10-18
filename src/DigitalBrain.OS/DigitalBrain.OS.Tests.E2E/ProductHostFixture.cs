using DigitalBrain;
using DigitalBrain.AI;
using DigitalBrain.AI.Models;
using DigitalBrain.Apps;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Aspire.Hosting;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Configuration;

namespace DigitalBrain.OS.Tests.E2E;

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
