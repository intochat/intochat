namespace IntoChat.OS.AI.SystemOne;

// A TypeSafe System One question. Choice picks one option, Score rates on a rubric, Noul asks
// whether a statement is true. A request mixes them; they are answered in parallel and in isolation.
[GenerateSerializer, Alias("ai.systemone.question")]
public abstract record SystemOneQuestion
{
    [GenerateSerializer, Alias("ai.systemone.question.Choice")]
    public sealed record Choice([property: Id(0)] string Instructions, [property: Id(1)] Dictionary<string, string?> Criteria) : SystemOneQuestion;

    [GenerateSerializer, Alias("ai.systemone.question.Score")]
    public sealed record Score([property: Id(0)] string Instructions, [property: Id(1)] string[] Criteria) : SystemOneQuestion;

    [GenerateSerializer, Alias("ai.systemone.question.Noul")]
    public sealed record Noul([property: Id(0)] string Instructions) : SystemOneQuestion;
}
