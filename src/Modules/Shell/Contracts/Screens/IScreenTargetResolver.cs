using DigitalBrain;

namespace IntoChat.OS.Modules.Flutter.Screens;

/// <summary>The resolved screen neuron and kind for an app name within a brain.</summary>
public sealed record ScreenTarget(NeuronId ScreenId, string Kind);

/// <summary>
/// Resolves an app identifier or a unit address within a brain to the screen that takes its commands: confirmations, a compiled
/// host screen, a runtime pure-data agent app screen, a sandboxed app's published view, or a synapse unit.
/// </summary>
public interface IScreenTargetResolver
{
    const string ConfirmationsKind = "confirmations";
    const string CompiledScreenKind = "screen";
    const string AgentAppKind = "agent";
    // A synapse unit's state neuron ({alias}/default), named by a unit address ({brain}/{alias} or {brain}/units/{alias}) or by
    // an app name that no compiled, agent or sandboxed app claims but a unit with that alias has published a view.
    const string UnitKind = "unit";
    // A sandboxed app's published view neuron (ui.view at apps/{name}/view): its Dispatch to "." becomes the app's request.
    const string ViewKind = "view";

    Task<ScreenTarget?> ResolveAsync(BrainId brainId, string appName, CancellationToken ct = default);
}
