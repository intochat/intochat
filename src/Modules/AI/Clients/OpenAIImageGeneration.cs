using System.ClientModel;
using IntoChat.OS.Modules.AI.Media;
using IntoChat.OS.Modules.AI.Models;
using OpenAI;
using OpenAI.Images;

namespace IntoChat.OS.Modules.AI;

internal sealed class OpenAIImageGeneration(ModelCard card, IAiCredentials credentials) : IImageGeneration, IConditionallyAvailable
{
    private const string DefaultMediaType = "image/png";

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => credentials.IsReady(AiIntegrations.IdOf(card.Provider), cancellationToken);

    public async Task<GeneratedUiImage> GenerateAsync(string prompt, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var material = await credentials.MaterializeAsync(AiIntegrations.IdOf(card.Provider), cancellationToken).ConfigureAwait(false);
        var apiKey = material.Values[AiIntegrations.ApiKeyField];
        var options = new OpenAIClientOptions();
        if (material.Snapshot.Settings.GetValueOrDefault(AiIntegrations.EndpointField) is { Length: > 0 } endpoint) { options.Endpoint = new Uri(endpoint); }
        var client = new OpenAIClient(new ApiKeyCredential(apiKey), options).GetImageClient(card.Id.Value);
        var image = await client.GenerateImageAsync(
            prompt,
            OptionsFor(card),
            cancellationToken).ConfigureAwait(false);

        var bytes = image.Value.ImageBytes
            ?? throw new InvalidOperationException($"{card.Name} returned no image bytes.");

        return new GeneratedUiImage(bytes.ToArray(), DefaultMediaType, card.Id.Value);
    }

    // Asking gpt-image-1 for a response format is HTTP 400 unknown_parameter: it
    // always answers with base64. Only older dall-e models accept ResponseFormat.
    internal static ImageGenerationOptions OptionsFor(ModelCard card)
    {
        ArgumentNullException.ThrowIfNull(card);

        return card.Id.Value.StartsWith("dall-e", StringComparison.OrdinalIgnoreCase)
            ? new ImageGenerationOptions { ResponseFormat = GeneratedImageFormat.Bytes }
            : new ImageGenerationOptions();
    }
}
