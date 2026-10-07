namespace DigitalBrain.AI.Models.OpenRouter;

internal sealed class OpenRouterProviderFactory : OpenAICompatibleProviderFactory
{
    public override AiProvider Provider => AiProvider.OpenRouter;
}
