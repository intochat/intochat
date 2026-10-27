using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;

namespace IntoChat.OS.Modules.AI.Agents;

// Plain calls stream the events as JSON (06-06d), so the abstract record names its cases.
[System.Text.Json.Serialization.JsonPolymorphic(TypeDiscriminatorPropertyName = "$event")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(Started), "started")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(ModelSelected), "model-selected")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(Text), "text")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(ToolStarted), "tool-started")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(ToolCompleted), "tool-completed")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(ToolFailed), "tool-failed")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(Completed), "completed")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(Finished), "finished")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(Failed), "failed")]
[GenerateSerializer, Alias("ai.turn-event")]
public abstract record AgentTurnEvent
{
    [GenerateSerializer, Alias("ai.turn-event.Started")]
    public sealed record Started([property: Id(0)] string RunId) : AgentTurnEvent;
    [GenerateSerializer, Alias("ai.turn-event.ModelSelected")]
    public sealed record ModelSelected([property: Id(0)] ResolvedModel Model, [property: Id(1)] ModelDescriptor Descriptor) : AgentTurnEvent;
    [GenerateSerializer, Alias("ai.turn-event.Text")]
    public sealed record Text([property: Id(0)] string Content) : AgentTurnEvent;
    [GenerateSerializer, Alias("ai.turn-event.ToolStarted")]
    public sealed record ToolStarted([property: Id(0)] string CallId, [property: Id(1)] string Name, [property: Id(2)] string Arguments) : AgentTurnEvent;
    [GenerateSerializer, Alias("ai.turn-event.ToolCompleted")]
    public sealed record ToolCompleted([property: Id(0)] string CallId, [property: Id(1)] string Name, [property: Id(2)] string Result) : AgentTurnEvent;
    [GenerateSerializer, Alias("ai.turn-event.ToolFailed")]
    public sealed record ToolFailed([property: Id(0)] string CallId, [property: Id(1)] string Name,
        [property: Id(2)] string Code = "tool_failed", [property: Id(3)] string Message = "The tool call failed.") : AgentTurnEvent;
    [GenerateSerializer, Alias("ai.turn-event.Completed")]
    public sealed record Completed([property: Id(0)] IReadOnlyList<AiMessage> Messages, [property: Id(1)] AgentUsage? Usage) : AgentTurnEvent;
    [GenerateSerializer, Alias("ai.turn-event.Finished")]
    public sealed record Finished : AgentTurnEvent;
    [GenerateSerializer, Alias("ai.turn-event.Failed")]
    public sealed record Failed([property: Id(0)] string Message, [property: Id(1)] bool Cancelled = false) : AgentTurnEvent;
}
