using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Modules.AI.Models;
using Microsoft.Extensions.AI;

namespace IntoChat.OS.Modules.AI.Voice;

internal interface IVoiceClients
{
    Task<Voice<ISpeechToTextClient>> Hear(string brain, CancellationToken ct);
    Task<Voice<ITextToSpeechClient>> Speak(string brain, CancellationToken ct);
    Task<TalkVoice> Talk(string brain, ConversationId conversation, CancellationToken ct);
}

internal sealed record Voice<T>(T? Client, ModelId? Model, string? Unavailable) where T : class;

internal sealed record TalkVoice(
    IRealtimeClient? Client,
    ModelId? Model,
    bool RecordsItself,
    int InputRate,
    string? Unavailable);
