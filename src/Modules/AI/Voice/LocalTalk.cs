using IntoChat.OS.Modules.Devices.Voice;
using Microsoft.Extensions.AI;

namespace IntoChat.OS.Modules.AI.Voice;

internal sealed record LocalTalkOptions
{
    public TimeSpan BargeIn { get; init; } = TimeSpan.FromMilliseconds(250);
    public string? Voice { get; init; }
    public string? Instructions { get; init; }
}

internal sealed class LocalTalk(
    ISpeechToTextClient hear,
    IChatClient chat,
    ITextToSpeechClient speak,
    IVoiceActivityDetector vad,
    LocalTalkOptions? options = null) : IRealtimeClient
{
    public static readonly RealtimeServerMessageType Interrupted = new("interrupted");

    internal ISpeechToTextClient Hear => hear;
    internal IChatClient Chat => chat;
    internal ITextToSpeechClient Speak => speak;
    internal IVoiceActivityDetector Vad => vad;
    internal LocalTalkOptions Options => options ?? new LocalTalkOptions();

    public Task<IRealtimeClientSession> CreateSessionAsync(
        RealtimeSessionOptions? sessionOptions = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("LocalTalk session pipeline is not implemented yet.");

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose() { }
}
