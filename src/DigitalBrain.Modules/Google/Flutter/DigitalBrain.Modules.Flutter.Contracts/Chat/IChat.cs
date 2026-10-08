using Orleans.Concurrency;

namespace DigitalBrain.Flutter.Chat;

// A recoverable UI projection. The source neuron owns the conversation and runs its turns.
[Alias("chat"), Orleans.Metadata.DefaultGrainType(UIVocabulary.ChatType)]
public interface IChat : INeuron
{
    Task Project(ChatProjection projection);
    [ReadOnly] Task<ChatState> Read();
}

[GenerateSerializer, Alias("ui.chat-projection")]
public sealed record ChatProjection(
    [property: Id(0)] string Source,
    [property: Id(1)] long Generation,
    [property: Id(2)] long Version,
    [property: Id(3)] ChatEntry[] Messages);

[GenerateSerializer, Alias("ui.chat-state")]
public sealed record ChatState
{
    [Id(0)] public string Name { get; init; } = "";
    [Id(1)] public long Revision { get; init; }
    [Id(2)] public ChatProjection? Projection { get; init; }
}

[GenerateSerializer, Alias("ui.chat-changed")]
public sealed record ChatChanged([property: Id(0)] string Name, [property: Id(1)] long Revision) : Signal;
