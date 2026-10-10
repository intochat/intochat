using DigitalBrain.AI;
using DigitalBrain.AI.Models;
using DigitalBrain.Flutter;

namespace DigitalBrain.OS.Testing;

// The reference brain flagged Production. Environment gates hide here, not in Development.
public class ProductionBrainFixture : ReferenceBrainFixture
{
    protected virtual bool SensitiveCapture => true;

    protected override Task<ITestBrain> StartBrainAsync(CancellationToken cancellationToken)
        => ReferenceBrain.Create()
            .WithOptions<AIModule, AIOptions>(ai =>
            {
                ai.WithModelEndpoint(AiProvider.OpenAI, Model.Endpoint);
                ai.Telemetry.EnableSensitiveData = SensitiveCapture;
            })
            .WithOptions<FlutterModule, FlutterModuleOptions>(flutter => flutter.BackendOnly())
            .InEnvironment("Production")
            .ExportingTelemetryTo(Collector)
            .StartAsync(cancellationToken);
}
