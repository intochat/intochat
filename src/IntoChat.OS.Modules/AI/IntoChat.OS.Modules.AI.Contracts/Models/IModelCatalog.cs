using IntoChat.OS;
using Orleans.Concurrency;

namespace IntoChat.OS.Modules.AI.Models;

// The deployment's model list as a neuron, so other modules and the shell read it without
// referencing the AI module. Never carries endpoints or secrets.
[Alias("ai.models"), Orleans.Metadata.DefaultGrainType("ai.models")]
[Public]
public interface IModelCatalog : INeuron
{
    // A brain's catalog: its list and choices count that brain's own provider keys. A bare key
    // ("default") is the deployment's view.
    static string Key(string brainId) => $"{brainId}/default";

    [ReadOnly, AlwaysInterleave] Task<ModelChoices> List(string? defaultModel = null);
    // Validates a choice id ("profile:<name>", "preset:<Marker>") for a tool-using turn.
    [ReadOnly, AlwaysInterleave] Task<ModelSelection?> Select(string? id, string? defaultModel = null);
}

[GenerateSerializer, Alias("ai.model-choices")]
public sealed record ModelChoices([property: Id(0)] ModelChoice Automatic, [property: Id(1)] ModelChoice[] Models);

[GenerateSerializer, Alias("ai.model-choice")]
public sealed record ModelChoice([property: Id(0)] string? Id, [property: Id(1)] string Label, [property: Id(2)] string? Provider,
    [property: Id(3)] string? Model, [property: Id(4)] bool Available, [property: Id(5)] string[] Capabilities)
{
    // Why an unavailable model cannot be chosen; null when available. Additive: older readers ignore it.
    [Id(6)] public string? Reason { get; init; }
}
