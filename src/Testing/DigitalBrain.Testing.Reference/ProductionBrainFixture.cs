using DigitalBrain.AI;
using DigitalBrain.AI.Models;
using DigitalBrain.Flutter;

namespace DigitalBrain.Testing.E2E;

// The reference brain flagged Production. Environment gates hide here, not in Development.
public class ProductionBrainFixture : ReferenceBrainFixture
{
    protected override Task<E2EBrain> StartBrainAsync(CancellationToken cancellationToken)
        => ReferenceBrain.Create()
            .ConfigureModule<AIModule, AIOptions>(ai => ai.WithModelEndpoint(AiProvider.OpenAI, Model.Endpoint))
            .ConfigureModule<FlutterModule, FlutterModuleOptions>(flutter => flutter.BackendOnly())
            .InEnvironment("Production")
            .ExportingTelemetryTo(Collector)
            .StartAsync(cancellationToken);
}
