using IntoChat.OS;

namespace IntoChat.OS.Modules.AI.Agents.Facts.Signals;

[GenerateSerializer, Alias("facts.asserted")]
public sealed record FactAsserted([property: Id(0)] string FactsId, [property: Id(1)] Guid FactId, [property: Id(2)] string DedupKey) : Signal;

[GenerateSerializer, Alias("facts.invalidated")]
public sealed record FactInvalidated([property: Id(0)] string FactsId, [property: Id(1)] Guid FactId, [property: Id(2)] Guid? SupersededBy) : Signal;

[GenerateSerializer, Alias("facts.forgotten")]
public sealed record FactForgotten([property: Id(0)] string FactsId, [property: Id(1)] Guid FactId) : Signal;
