namespace DigitalBrain.AI.Models.XAI;

internal sealed class XAIProviderFactory : OpenAICompatibleProviderFactory
{
    public override AiProvider Provider => AiProvider.XAI;
}
