namespace IntoChat.OS.AI.SystemOne;

// The typed answer to a System One question. Choice and Score carry a full probability
// distribution and a confidence (how peaked that distribution is); Noul carries a truth value.
[GenerateSerializer, Alias("ai.systemone.answer")]
public abstract record SystemOneAnswer
{
    [GenerateSerializer, Alias("ai.systemone.answer.Choice")]
    public sealed record Choice([property: Id(0)] string Value, [property: Id(1)] Dictionary<string, double> Probabilities, [property: Id(2)] double Confidence) : SystemOneAnswer;

    [GenerateSerializer, Alias("ai.systemone.answer.Score")]
    public sealed record Score([property: Id(0)] double Value, [property: Id(1)] Dictionary<string, double> Probabilities, [property: Id(2)] double Confidence) : SystemOneAnswer;

    [GenerateSerializer, Alias("ai.systemone.answer.Noul")]
    public sealed record Noul([property: Id(0)] double Value) : SystemOneAnswer;
}
