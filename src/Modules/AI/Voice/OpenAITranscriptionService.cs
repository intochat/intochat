using System.ClientModel;
using IntoChat.OS.Modules.AI.Models;
using OpenAI;
using OpenAI.Audio;

namespace IntoChat.OS.Modules.AI;

// Hosted speech-to-text over the provider's own audio endpoint. Deliberately not
// an IHostedService: there is no model to download or load, which is the whole
// reason this exists alongside the Foundry Local path.
internal sealed class OpenAITranscriptionService : IAudioTranscriptionService
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);

    private static readonly string[] AcceptedExtensions =
        [".flac", ".m4a", ".mp3", ".mp4", ".mpeg", ".mpga", ".oga", ".ogg", ".wav", ".webm"];

    private const string FallbackFileName = "voice.wav";
    private const string OpusExtension = ".opus";

    private readonly ModelCard _card;
    private readonly IAiCredentials _credentials;

    internal OpenAITranscriptionService(ModelCard card, IAiCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(credentials);

        _card = card;
        _credentials = credentials;
    }

    private string Integration => AiIntegrations.IdOf(_card.Provider);

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
        => _card.Can.HasFlag(Abilities.Hear) && await _credentials.IsReady(Integration, cancellationToken);

    public bool InitializationFailed => !_card.Can.HasFlag(Abilities.Hear);

    public string? ErrorMessage => _card.Can.HasFlag(Abilities.Hear)
        ? null
        : $"{_card.Name} does not support speech transcription.";

    public string ModelId => _card.Id.Value;

    public async Task<string> TranscribeAsync(
        string audioFilePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioFilePath);

        var stream = File.OpenRead(audioFilePath);
        await using (stream.ConfigureAwait(false))
        {
            return await TranscribeAsync(stream, Path.GetFileName(audioFilePath), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task<string> TranscribeAsync(
        Stream audioStream,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audioStream);

        if (!await IsReadyAsync(cancellationToken))
        {
            throw new InvalidOperationException(ErrorMessage ?? "Transcription is not configured.");
        }

        var client = await CreateClientAsync(cancellationToken).ConfigureAwait(false);
        var options = new AudioTranscriptionOptions { ResponseFormat = AudioTranscriptionFormat.Text };

        var transcription = await client
            .TranscribeAudioAsync(audioStream, NormalizeFileName(fileName), options, cancellationToken)
            .ConfigureAwait(false);

        return transcription.Value.Text ?? string.Empty;
    }

    private async Task<AudioClient> CreateClientAsync(CancellationToken cancellationToken)
    {
        var material = await _credentials.MaterializeAsync(Integration, cancellationToken).ConfigureAwait(false);
        var apiKey = material.Values[AiIntegrations.ApiKeyField];
        var options = new OpenAIClientOptions { NetworkTimeout = RequestTimeout };
        if (material.Snapshot.Settings.GetValueOrDefault(AiIntegrations.EndpointField) is { Length: > 0 } endpoint)
        {
            options.Endpoint = new Uri(endpoint);
        }

        return new AudioClient(_card.Id.Value, new ApiKeyCredential(apiKey), options);
    }

    internal static string NormalizeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return FallbackFileName;
        }

        var extension = Path.GetExtension(fileName);
        if (AcceptedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return fileName;
        }

        if (OpusExtension.Equals(extension, StringComparison.OrdinalIgnoreCase))
        {
            return Path.ChangeExtension(fileName, ".ogg");
        }

        return FallbackFileName;
    }
}
