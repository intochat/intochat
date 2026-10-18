namespace IntoChat.OS.AI.SystemOne;

// One entry in Answers per question id from the request.
[GenerateSerializer, Alias("ai.systemone.result")]
public sealed record SystemOneResult(
    [property: Id(0)] string Model,
    [property: Id(1)] Dictionary<string, SystemOneAnswer> Answers,
    [property: Id(2)] int InputTokens = 0,
    [property: Id(3)] int OutputTokens = 0);
