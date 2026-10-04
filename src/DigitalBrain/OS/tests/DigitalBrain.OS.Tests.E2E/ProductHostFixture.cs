using DigitalBrain.AI;
using DigitalBrain.Flutter;

namespace DigitalBrain.OS.Tests.E2E;

// Product-only shipped content; all lease, model, browser and telemetry support is shared.
public sealed class ProductHostFixture : ReferenceBrainFixture
{
    public static readonly string[] ShippedPackages = ["os/settings", "os/assistant", "intochat/customer-researcher"];

    protected override Task<E2EBrain> StartBrainAsync(CancellationToken cancellationToken)
        => ProductE2E.Create()
            .ConfigureModule<AIModule, AIOptions>(ai => ai.WithModelEndpoint(AiProvider.OpenAI, Model.Endpoint))
            .ConfigureModule<FlutterModule, FlutterModuleOptions>(flutter => flutter.RunWebApp())
            .WithHostConfiguration("DigitalBrain:Flutter:Hosting:WebContainer", "true")
            .WithBrowser(new() { PrimarySession = false, AssertionTimeout = TimeSpan.FromMinutes(2) })
            .WithStartupTimeout(TimeSpan.FromMinutes(15))
            .WithResourceEnvironment(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = Collector.Endpoint.AbsoluteUri,
                ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
                ["DigitalBrain__Apps__ShipOnStartup"] = "settings,assistant,customer-researcher",
            })
            .StartAsync(cancellationToken);
}
