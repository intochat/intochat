namespace IntoChat.OS.Modules.Apps;

// Builds an app from a description in one call: the Author writes the spec, the Builder writes the
// behaviors, and the call answers the app once it is Live or Failed. A single call so a router can
// route to it and the shell can await one invocation.
// A plain neuron (06-08), the brain's one studio at {brain}/main (R4-19 singleton key; clean break from the bare brain key).
[DigitalBrain.Contracts.NeuronAlias("apps.studio"), DigitalBrain.Contracts.DefaultNeuronType("apps.studio")]
public interface IAppStudio
{
    static string Key(string brainId) => $"{brainId}/main";

    // Create the app from a description; answer it Drafted, without behaviors.
    [DigitalBrain.Interleaves, DigitalBrain.CallTimeout(600_000)] Task<AppSnapshot> Author(string request, CancellationToken ct = default);

    // Author if needed, then build the behaviors and answer the app once it is Live or Failed.
    [DigitalBrain.Interleaves, DigitalBrain.CallTimeout(600_000)] Task<AppSnapshot> Build(string request, CancellationToken ct = default);
}
