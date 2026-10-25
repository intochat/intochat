using IntoChat.OS;

namespace IntoChat.OS.Modules.AI.Agents.Context.Signals;

[GenerateSerializer, Alias("agent-context.updated")]
public sealed record ContextUpdated([property: Id(0)] string ContextId, [property: Id(1)] long Revision, [property: Id(2)] string Name) : Signal;

[GenerateSerializer, Alias("agent-context.removed")]
public sealed record ContextRemoved([property: Id(0)] string ContextId, [property: Id(1)] long Revision, [property: Id(2)] string Name) : Signal;
