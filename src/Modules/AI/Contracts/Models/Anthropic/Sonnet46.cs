using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.AI.Models.Anthropic;

public sealed class Sonnet46 : LLMModel<ISonnet46>
{
    public override string Id => "claude-sonnet-4-6";

    public override AiProvider Provider => AiProvider.Anthropic;
}

[NeuronAlias("ai.llm.sonnet46"), DefaultNeuronType("ai.llm.sonnet46")]
public interface ISonnet46 : ILLM;
