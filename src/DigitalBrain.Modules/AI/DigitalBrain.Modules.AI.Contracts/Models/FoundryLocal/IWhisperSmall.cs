using DigitalBrain.AI.Voice;

namespace DigitalBrain.AI.Models.FoundryLocal;

public sealed class WhisperSmall : TranscriptionModel<IWhisperSmall>
{
    public override string Id => "whisper-small";

    public override AiProvider Provider => AiProvider.FoundryLocal;
}

public interface IWhisperSmall : ITranscription;
