using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.AI.Models.Anthropic;

public sealed class Opus46 : LLMModel<IOpus46>
{
    public override string Id => "claude-opus-4-6";

    public override AiProvider Provider => AiProvider.Anthropic;
}

[NeuronAlias("ai.llm.opus46"), DefaultNeuronType("ai.llm.opus46")]
public interface IOpus46 : ILLM;
