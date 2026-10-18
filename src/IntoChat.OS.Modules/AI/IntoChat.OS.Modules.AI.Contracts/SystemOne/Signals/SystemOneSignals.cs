using IntoChat.OS;

namespace IntoChat.OS.AI.SystemOne.Signals;

// One System One answer, as a fact apps and the shell can subscribe to. RequestSummary is the state
// clipped to 2000 characters; Confidence is the highest Choice/Score confidence in Answers (0 if none).
[GenerateSerializer, Alias("ai.systemone-decided")]
public sealed record SystemOneDecided(
    [property: Id(0)] string RequestSummary,
    [property: Id(1)] Dictionary<string, SystemOneAnswer> Answers,
    [property: Id(2)] double Confidence) : Signal;
