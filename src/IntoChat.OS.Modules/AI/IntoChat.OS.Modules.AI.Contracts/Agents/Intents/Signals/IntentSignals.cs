using IntoChat.OS;

namespace IntoChat.OS.AI.Agents.Intents.Signals;

[GenerateSerializer, Alias("intents.heard")]
public sealed record IntentHeard([property: Id(0)] string IntentsId, [property: Id(1)] Guid IntentId, [property: Id(2)] string RequestId) : Signal;

[GenerateSerializer, Alias("intents.rephrased")]
public sealed record IntentRephrased([property: Id(0)] string IntentsId, [property: Id(1)] Guid IntentId, [property: Id(2)] string RequestId, [property: Id(3)] double Confidence) : Signal;

[GenerateSerializer, Alias("intents.transitioned")]
public sealed record IntentTransitioned([property: Id(0)] string IntentsId, [property: Id(1)] Guid IntentId, [property: Id(2)] IntentState From, [property: Id(3)] IntentState To) : Signal;

[GenerateSerializer, Alias("intents.armed")]
public sealed record IntentArmed([property: Id(0)] string IntentsId, [property: Id(1)] Guid IntentId) : Signal;

[GenerateSerializer, Alias("intents.cue-fired")]
public sealed record IntentCueFired([property: Id(0)] string IntentsId, [property: Id(1)] Guid IntentId, [property: Id(2)] string Cue) : Signal;
