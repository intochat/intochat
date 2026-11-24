using DigitalBrain;

namespace IntoChat.OS.Modules.Flutter.Screens;

/// <summary>The resolved screen neuron and kind for an app name within a brain.</summary>
public sealed record ScreenTarget(NeuronId ScreenId, string Kind);

/// <summary>
/// Resolves an app identifier within a brain to its concrete screen target: confirmations, a compiled host screen,
/// or a runtime pure-data agent app screen.
/// </summary>
public interface IScreenTargetResolver
{
    const string ConfirmationsKind = "confirmations";
    const string CompiledScreenKind = "screen";
    const string AgentAppKind = "agent";

    Task<ScreenTarget?> ResolveAsync(BrainId brainId, string appName, CancellationToken ct = default);
}
