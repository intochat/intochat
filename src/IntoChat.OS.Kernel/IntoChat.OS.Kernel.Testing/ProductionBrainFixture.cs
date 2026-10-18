using IntoChat.OS.AI;
using IntoChat.OS.AI.Models;
using IntoChat.OS.Flutter;

namespace IntoChat.OS.Kernel.Testing;

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
            // R81: the AppHost's own machines share one configured custody root (R74); production accepts it only acknowledged.
            .WithConfiguration(IntoChat.OS.Framework.IntoChatOSNames.ConfiguredCustodyAcknowledgedConfigurationKey, "true")
            .WithDashboardTelemetry()
            .StartAsync(cancellationToken);
}
