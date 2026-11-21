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

    private readonly LocalTalkOptions _options = options ?? new LocalTalkOptions();

    public Task<IRealtimeClientSession> CreateSessionAsync(
        RealtimeSessionOptions? sessionOptions = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var activity = vad.Open(LocalTalkSession.InputSampleRate);
        IRealtimeClientSession session = new LocalTalkSession(hear, chat, speak, activity, _options, sessionOptions);
        return Task.FromResult(session);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose() { }
}
