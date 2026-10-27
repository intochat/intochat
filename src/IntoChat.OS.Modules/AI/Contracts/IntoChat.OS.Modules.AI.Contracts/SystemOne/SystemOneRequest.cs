namespace IntoChat.OS.Modules.AI.SystemOne;

// Unstructured state plus typed questions. Model overrides the deployment's default System One model.
[GenerateSerializer, Alias("ai.systemone.request")]
public sealed record SystemOneRequest(
    [property: Id(0)] string State,
    [property: Id(1)] Dictionary<string, SystemOneQuestion> Questions,
    [property: Id(2)] string? Model = null);
