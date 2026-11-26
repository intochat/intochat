namespace IntoChat.OS.Modules.Devices.Voice;

public interface IVoiceActivityDetector
{
    bool Ready { get; }

    IVoiceActivity Open(int sampleRate = 16_000);
}

public interface IVoiceActivity : IDisposable
{
    VoiceActivity Push(ReadOnlySpan<byte> pcm16);
}

public readonly record struct VoiceActivity(bool Speaking, TimeSpan SpeakingFor, ReadOnlyMemory<byte>[] Ended);
