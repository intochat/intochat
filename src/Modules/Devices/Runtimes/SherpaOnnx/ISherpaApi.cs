namespace IntoChat.OS.Modules.Devices.Runtimes.SherpaOnnx;

internal interface ISherpaApi
{
    ISherpaTts CreateTts(string modelDirectory);

    ISherpaVad CreateVad(string modelPath, int sampleRate);
}

internal interface ISherpaTts : IDisposable
{
    int SampleRate { get; }

    float[] Synthesize(string text, float speed, int speakerId, CancellationToken ct);
}

internal interface ISherpaVad : IDisposable
{
    int SampleRate { get; }

    int WindowSize { get; }

    void AcceptWaveform(float[] samples);

    bool IsSpeechDetected();

    bool IsEmpty();

    float[] PopSegment();

    void Flush();
}
