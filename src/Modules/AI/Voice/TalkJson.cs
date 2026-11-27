using System.Text.Json.Serialization;

namespace IntoChat.OS.Modules.AI.Voice;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TalkState), "state")]
[JsonDerivedType(typeof(TalkHeard), "heard")]
[JsonDerivedType(typeof(TalkReply), "reply")]
[JsonDerivedType(typeof(TalkCancel), "cancel")]
[JsonDerivedType(typeof(TalkTool), "tool")]
[JsonDerivedType(typeof(TalkError), "error")]
[JsonDerivedType(typeof(TalkInterrupt), "interrupt")]
[JsonDerivedType(typeof(TalkEnd), "end")]
internal abstract record TalkEvent;

internal sealed record TalkState(string State) : TalkEvent;      // listening | thinking | speaking

internal sealed record TalkHeard(string Text) : TalkEvent;

internal sealed record TalkReply(string Text) : TalkEvent;       // a text delta of the reply

internal sealed record TalkCancel : TalkEvent;                   // drop queued playback (barge-in)

internal sealed record TalkTool(string CallId, string Name, string Status, string? Label = null) : TalkEvent; // running | done | failed

internal sealed record TalkError(string Message) : TalkEvent;

internal sealed record TalkInterrupt : TalkEvent;                // client -> Kernel: tap-to-interrupt

internal sealed record TalkEnd : TalkEvent;                      // client -> Kernel: close

internal sealed record SpeechRequest(string Text, string? Voice = null);

internal sealed record TranscriptionResponse(string Text, string Model);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TalkEvent))]
[JsonSerializable(typeof(SpeechRequest))]
[JsonSerializable(typeof(TranscriptionResponse))]
internal sealed partial class TalkJson : JsonSerializerContext;
