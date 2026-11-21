namespace IntoChat.OS.Modules.AI.Models;

// Model is a card id ("qwen3.5:4b"), legacy marker name ("IFable5"), or a configured profile name;
// null means the brain's active role default. Reasoning and MaxOutputTokens override what the model or profile pins.
public sealed record ModelSelection(
    string? Model = null,
    string? Reasoning = null,
    int? MaxOutputTokens = null);

// Everything a turn needs to build its client, minus credentials, which are resolved at use.
public sealed record ResolvedModel(
    AiProvider Provider,
    string Model,
    string? Endpoint,
    string? Profile,
    string Revision,
    Abilities Capabilities,
    string? Reasoning,
    int? MaxOutputTokens);
