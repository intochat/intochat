namespace IntoChat.OS.Modules.AI.Models;

// Model is a marker name ("IFable5") or a configured profile name; null is the
// deployment default. Reasoning and MaxOutputTokens override what the model or profile pins.
[GenerateSerializer, Alias("ai.model-selection")]
public sealed record ModelSelection(
    [property: Id(0)] string? Model = null,
    [property: Id(1)] string? Reasoning = null,
    [property: Id(2)] int? MaxOutputTokens = null);

// Everything a turn needs to build its client, minus credentials, which are resolved at use.
[GenerateSerializer, Alias("ai.resolved-model")]
public sealed record ResolvedModel(
    [property: Id(0)] AiProvider Provider,
    [property: Id(1)] string Model,
    [property: Id(2)] string? Endpoint,
    [property: Id(3)] string? Profile,
    [property: Id(4)] string Revision,
    [property: Id(5)] LlmCapabilities Capabilities,
    [property: Id(6)] string? Reasoning,
    [property: Id(7)] int? MaxOutputTokens);
