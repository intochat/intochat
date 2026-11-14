using DigitalBrain.Access;
using DigitalBrain.Contracts;
using IntoChat.OS;

namespace IntoChat.OS.Modules.AI.Models;

// The deployment's model list as a neuron, so other modules and the shell read it without
// referencing the AI module. Never carries endpoints or secrets.
[NeuronAlias("ai.models"), DefaultNeuronType("ai.models")]
[Public]
public interface IModelCatalog
{
    // A brain's catalog: its list and choices count that brain's own provider keys. The deployment's view is the
    // catalog of the reserved system brain (R4-19: a cluster-scoped singleton lives there, keyed main).
    static string Key(string brainId) => $"{brainId}/main";

    const string SystemBrain = "system";

    static string DeploymentKey => Key(SystemBrain);

    [DigitalBrain.Contracts.ReadOnlyCall, DigitalBrain.Contracts.Interleaves] Task<ModelChoices> List(string? defaultModel = null);
    // Validates a choice id ("profile:<name>", "preset:<Marker>") for a tool-using turn.
    [DigitalBrain.Contracts.ReadOnlyCall, DigitalBrain.Contracts.Interleaves] Task<ModelSelection?> Select(string? id, string? defaultModel = null);
    // Probes local Ollama availability without exposing endpoints or secrets.
    [DigitalBrain.Contracts.ReadOnlyCall, DigitalBrain.Contracts.Interleaves] Task<LocalModelSummary> DetectLocal();
    // Stores a brain-scoped provider credential in the durable platform entry store.
    Task SaveProviderCredential(string provider, string credentialValue);
}

public sealed record LocalModelSummary(bool Available, string? PreferredModel, string[] InstalledModels);

public sealed record ModelChoices(ModelChoice Automatic, ModelChoice[] Models);

public sealed record ModelChoice(string? Id, string Label, string? Provider,
    string? Model, bool Available, string[] Capabilities)
{
    // Why an unavailable model cannot be chosen; null when available. Additive: older readers ignore it.
    public string? Reason { get; init; }

    // Additive visual identity fields resolved via VisualCatalog so model pickers and badges render crisp brand icons.
    public string? IconKey { get; init; }

    public string? ProviderLabel { get; init; }

    public string? AccentHex { get; init; }
}
