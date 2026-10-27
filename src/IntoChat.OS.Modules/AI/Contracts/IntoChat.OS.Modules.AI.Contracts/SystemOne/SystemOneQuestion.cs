using System.Text.Json.Serialization;

namespace IntoChat.OS.Modules.AI.SystemOne;

// A TypeSafe System One question. Choice picks one option, Score rates on a rubric, Noul asks
// whether a statement is true. A request mixes them; they are answered in parallel and in isolation.
// The JSON form (plain neuron calls and signals, 06-05b) names the case, as the Orleans form names its alias.
[GenerateSerializer, Alias("ai.systemone.question")]
[JsonPolymorphic, JsonDerivedType(typeof(Choice), "choice"), JsonDerivedType(typeof(Score), "score"), JsonDerivedType(typeof(Noul), "noul")]
public abstract record SystemOneQuestion
{
    [GenerateSerializer, Alias("ai.systemone.question.Choice")]
    public sealed record Choice([property: Id(0)] string Instructions, [property: Id(1)] Dictionary<string, string?> Criteria) : SystemOneQuestion;

    [GenerateSerializer, Alias("ai.systemone.question.Score")]
    public sealed record Score([property: Id(0)] string Instructions, [property: Id(1)] string[] Criteria) : SystemOneQuestion;

    [GenerateSerializer, Alias("ai.systemone.question.Noul")]
    public sealed record Noul([property: Id(0)] string Instructions) : SystemOneQuestion;
}
