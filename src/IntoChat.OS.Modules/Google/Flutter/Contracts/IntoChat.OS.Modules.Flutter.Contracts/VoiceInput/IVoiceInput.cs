using DigitalBrain.Contracts;
using DigitalBrain;
using IntoChat.OS;
using DigitalBrain.State;

namespace IntoChat.OS.Modules.Flutter.VoiceInput;

// Captures audio from the user; whoever listens decides how to transcribe it.
[NeuronAlias("voiceinput"), DefaultNeuronType(UIVocabulary.VoiceInputType)]
public interface IVoiceInput
{
    const int MaxAudioBytes = 8 * 1024 * 1024;

    Task Configure(string label);
    Task Capture(byte[] audio, string mimeType);
    [DigitalBrain.ReadOnlyCall] Task<VoiceInputState> Read();
}

[GenerateSerializer, Alias("ui.voiceinput-state")]
[ExportableState]
public sealed class VoiceInputState
{
    [Id(0)] public string Name { get; set; } = "";
    [Id(1)] public long Revision { get; set; }
    [Id(2)] public string Label { get; set; } = "";
    [Id(3)] public int Captures { get; set; }
}
