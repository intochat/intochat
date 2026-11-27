using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace IntoChat.OS.Modules.AI.Voice;

internal sealed class GeminiTextToSpeechClient : ITextToSpeechClient
{
    internal static readonly Uri DefaultBaseUri = new("https://generativelanguage.googleapis.com");
    internal const string DefaultVoice = "Kore";
    private const int DefaultSampleRate = 24_000;

    private static readonly string[] GeminiVoices =
    [
        "Kore", "Puck", "Aoede", "Charon", "Fenrir", "Leda", "Orus", "Zephyr",
    ];

    private readonly string _modelId;
    private readonly string _apiKey;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Uri _baseUri;
    private readonly TextToSpeechClientMetadata _metadata;

    public GeminiTextToSpeechClient(
        string modelId,
        string apiKey,
        HttpClient? httpClient = null,
        Uri? baseUri = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        _modelId = modelId.Trim();
        _apiKey = apiKey.Trim();
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        _baseUri = baseUri ?? DefaultBaseUri;
        _metadata = new TextToSpeechClientMetadata("google", _baseUri, _modelId);
    }

    internal static string ResolveGeminiVoice(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return DefaultVoice;
        }

        var trimmed = requested.Trim();
        return GeminiVoices.FirstOrDefault(v => string.Equals(v, trimmed, StringComparison.OrdinalIgnoreCase))
            ?? DefaultVoice;
    }

    public async Task<TextToSpeechResponse> GetAudioAsync(
        string text,
        TextToSpeechOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var model = string.IsNullOrWhiteSpace(options?.ModelId) ? _modelId : options.ModelId.Trim();
        var voice = ResolveGeminiVoice(options?.VoiceId);
        var endpoint = new Uri(_baseUri, $"/v1beta/models/{Uri.EscapeDataString(model)}:generateContent");

        var payload = BuildRequestJson(text, voice);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("x-goog-api-key", _apiKey);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Google Gemini TTS request failed with HTTP {(int)response.StatusCode}.");
        }

        var (audioBytes, mediaType) = ExtractAudioFromResponse(body);
        if (audioBytes.Length == 0)
        {
            throw new InvalidOperationException("Google Gemini TTS returned no audio payload.");
        }

        return new TextToSpeechResponse([new DataContent(audioBytes, mediaType)])
        {
            ModelId = model,
        };
    }

    public async IAsyncEnumerable<TextToSpeechResponseUpdate> GetStreamingAudioAsync(
        string text,
        TextToSpeechOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetAudioAsync(text, options, cancellationToken).ConfigureAwait(false);
        yield return new TextToSpeechResponseUpdate(response.Contents)
        {
            Kind = TextToSpeechResponseUpdateKind.AudioUpdating,
            ModelId = response.ModelId,
        };
        yield return new TextToSpeechResponseUpdate
        {
            Kind = TextToSpeechResponseUpdateKind.SessionClose,
            ModelId = response.ModelId,
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType == typeof(TextToSpeechClientMetadata))
        {
            return _metadata;
        }

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    internal static string BuildRequestJson(string text, string voice)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("contents");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WritePropertyName("parts");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("text", text);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();

            writer.WritePropertyName("generationConfig");
            writer.WriteStartObject();
            writer.WritePropertyName("responseModalities");
            writer.WriteStartArray();
            writer.WriteStringValue("AUDIO");
            writer.WriteEndArray();
            writer.WritePropertyName("speechConfig");
            writer.WriteStartObject();
            writer.WritePropertyName("voiceConfig");
            writer.WriteStartObject();
            writer.WritePropertyName("prebuiltVoiceConfig");
            writer.WriteStartObject();
            writer.WriteString("voiceName", voice);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static (byte[] Audio, string MediaType) ExtractAudioFromResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array)
        {
            return ([], $"audio/pcm;rate={DefaultSampleRate}");
        }

        foreach (var candidate in candidates.EnumerateArray())
        {
            if (!candidate.TryGetProperty("content", out var content)
                || !content.TryGetProperty("parts", out var parts)
                || parts.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var part in parts.EnumerateArray())
            {
                if (!part.TryGetProperty("inlineData", out var inlineData)
                    && !part.TryGetProperty("inline_data", out inlineData))
                {
                    continue;
                }

                if (!inlineData.TryGetProperty("data", out var dataElement) || dataElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var base64 = dataElement.GetString();
                if (string.IsNullOrWhiteSpace(base64))
                {
                    continue;
                }

                var bytes = Convert.FromBase64String(base64);
                var rawMime = inlineData.TryGetProperty("mimeType", out var mimeElement) && mimeElement.ValueKind == JsonValueKind.String
                    ? mimeElement.GetString()
                    : inlineData.TryGetProperty("mime_type", out var snakeMime) && snakeMime.ValueKind == JsonValueKind.String
                        ? snakeMime.GetString()
                        : null;
                return (bytes, NormalizeAudioMediaType(rawMime));
            }
        }

        return ([], $"audio/pcm;rate={DefaultSampleRate}");
    }

    private static string NormalizeAudioMediaType(string? rawMime)
    {
        if (string.IsNullOrWhiteSpace(rawMime))
        {
            return $"audio/pcm;rate={DefaultSampleRate}";
        }

        var trimmed = rawMime.Trim();
        if (trimmed.StartsWith("audio/L16", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("audio/pcm", StringComparison.OrdinalIgnoreCase))
        {
            var rate = SpeechEndpoint.ParsePcmRate(trimmed);
            return $"audio/pcm;rate={rate}";
        }

        return trimmed;
    }
}
