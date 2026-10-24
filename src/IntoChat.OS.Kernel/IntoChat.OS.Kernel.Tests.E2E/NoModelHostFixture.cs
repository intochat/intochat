using IntoChat.OS.AI;
using IntoChat.OS.AI.Models;
using IntoChat.OS.AI.Models.Ollama;
using IntoChat.OS.Flutter;

namespace IntoChat.OS.Kernel.Tests.E2E;

// A brain with no usable model, as a fresh local desktop run without Ollama is: no provider key (so
// Automatic has nothing to resolve) and a configured local model (IQwen35) whose server is not set up, so
// the catalog lists it unavailable. Ino shows its first-run card here, never a failed turn.
public sealed class NoModelHostFixture : ReferenceBrainFixture
{
    protected override Task<ITestBrain> StartBrainAsync(CancellationToken cancellationToken)
        => ReferenceBrain.Create(modelApiKey: "")
            .WithOptions<AIModule, AIOptions>(ai =>
            {
                ai.Ollama.Endpoint = null;
                ai.Ollama.Models[nameof(IQwen35)] = new AIModelOptions { Model = RunProfileModel };
            })
            .WithOptions<FlutterModule, FlutterModuleOptions>(flutter => flutter.BackendOnly())
            .WithDashboardTelemetry()
            .StartAsync(cancellationToken);

    private static string RunProfileModel => new Qwen35().Id;
}
