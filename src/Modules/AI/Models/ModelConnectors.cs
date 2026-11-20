using DigitalBrain.Capabilities.Connections.Connectors;
using IntoChat.OS.Modules.AI.Media;
using IntoChat.OS.Modules.AI.Models.Signals;

namespace IntoChat.OS.Modules.AI.Models;

internal static class ModelConnectors
{
    public static ConnectorDefinition[] All { get; } = [.. AiIntegrations.Definitions.Select(Connector)];

    private static ConnectorDefinition Connector(IntegrationSchema schema)
    {
        if (schema.Id == AiIntegrations.TavilyId) { return new(schema.Id, schema.DisplayName, ["api-key"], [schema.Id]); }
        var provider = AiIntegrations.KeyedProviders.Single(provider => AiIntegrations.IdOf(provider) == schema.Id);
        Type[] media = provider switch
        {
            AiProvider.OpenAI => [typeof(IImageGenerator), typeof(IEmbeddingModel), typeof(ISpeechRecognizer), typeof(ISpeechSynthesizer)],
            AiProvider.Google => [typeof(IEmbeddingModel)],
            _ => [],
        };
        Type[] signals = provider switch
        {
            AiProvider.OpenAI => [typeof(ImageGenerated), typeof(EmbeddingsGenerated), typeof(SpeechRecognized), typeof(SpeechSynthesized), typeof(MediaOperationFailed)],
            AiProvider.Google => [typeof(EmbeddingsGenerated), typeof(MediaOperationFailed)],
            _ => [],
        };
        return new(schema.Id, schema.DisplayName, ["api-key"], [schema.Id])
        {
            Neurons = [typeof(ILLM), .. media],
            Signals = [typeof(InferenceStarted), typeof(InferenceCompleted), typeof(InferenceFailed), .. signals],
        };
    }
}
