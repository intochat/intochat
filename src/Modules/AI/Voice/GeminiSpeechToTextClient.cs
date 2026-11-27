using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using IntoChat.OS.Modules.AI.Media;
using Microsoft.Extensions.AI;

namespace IntoChat.OS.Modules.AI.Voice;

internal sealed class GeminiSpeechToTextClient : ISpeechToTextClient
{
    private const string TranscribePrompt =
        "Transcribe the spoken audio accurately. Return only the transcription text without commentary.";

    private readonly string _modelId;
    private readonly string _apiKey;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Uri _baseUri;
    private readonly SpeechToTextClientMetadata _metadata;

    public GeminiSpeechToTextClient(
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
        _baseUri = baseUri ?? GeminiTextToSpeechClient.DefaultBaseUri;
        _metadata = new SpeechToTextClientMetadata("google", _baseUri, _modelId);
    }

    public async Task<SpeechToTextResponse> GetTextAsync(
        Stream audioSpeechStream,
        SpeechToTextOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audioSpeechStream);

        var audioBytes = await ReadBoundedAudioAsync(audioSpeechStream, MediaNeuron.MaxPayloadBytes, cancellationToken).ConfigureAwait(false);
        var mimeType = DetectAudioMimeType(audioBytes);
        var model = string.IsNullOrWhiteSpace(options?.ModelId) ? _modelId : options.ModelId.Trim();
        var endpoint = new Uri(_baseUri, $"/v1beta/models/{Uri.EscapeDataString(model)}:generateContent");

        var payload = BuildRequestJson(mimeType, Convert.ToBase64String(audioBytes));
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
            throw new InvalidOperationException($"Google Gemini STT request failed with HTTP {(int)response.StatusCode}.");
        }

        var text = ExtractTranscript(body);
        return new SpeechToTextResponse(text)
        {
            ModelId = model,
        };
    }

    public async IAsyncEnumerable<SpeechToTextResponseUpdate> GetStreamingTextAsync(
        Stream audioSpeechStream,
        SpeechToTextOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetTextAsync(audioSpeechStream, options, cancellationToken).ConfigureAwait(false);
        yield return new SpeechToTextResponseUpdate(response.Text)
        {
            Kind = SpeechToTextResponseUpdateKind.TextUpdated,
            ModelId = response.ModelId,
        };
        yield return new SpeechToTextResponseUpdate
        {
            Kind = SpeechToTextResponseUpdateKind.SessionClose,
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

        if (serviceType == typeof(SpeechToTextClientMetadata))
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

    internal static string BuildRequestJson(string mimeType, string base64Audio)
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
            writer.WriteString("text", TranscribePrompt);
            writer.WriteEndObject();

            writer.WriteStartObject();
            writer.WritePropertyName("inlineData");
            writer.WriteStartObject();
            writer.WriteString("mimeType", mimeType);
            writer.WriteString("data", base64Audio);
            writer.WriteEndObject();
            writer.WriteEndObject();

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static string ExtractTranscript(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
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
                if (part.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
                {
                    var text = textElement.GetString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        sb.Append(text);
                    }
                }
            }

            if (sb.Length > 0)
            {
                break;
            }
        }

        return sb.ToString().Trim();
    }

    internal static string DetectAudioMimeType(ReadOnlySpan<byte> audio)
    {
        if (audio.Length >= 4)
        {
            if (audio[0] == (byte)'R' && audio[1] == (byte)'I' && audio[2] == (byte)'F' && audio[3] == (byte)'F')
            {
                return "audio/wav";
            }

            if (audio[0] == (byte)'O' && audio[1] == (byte)'g' && audio[2] == (byte)'g' && audio[3] == (byte)'S')
            {
                return "audio/ogg";
            }

            if (audio[0] == (byte)'f' && audio[1] == (byte)'L' && audio[2] == (byte)'a' && audio[3] == (byte)'C')
            {
                return "audio/flac";
            }

            if ((audio[0] == (byte)'I' && audio[1] == (byte)'D' && audio[2] == (byte)'3')
                || (audio[0] == 0xFF && (audio[1] & 0xE0) == 0xE0))
            {
                return "audio/mpeg";
            }
        }

        return "audio/wav";
    }

    private static async Task<byte[]> ReadBoundedAudioAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        var total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maxBytes)
            {
                throw new ArgumentException("Audio payload exceeds the 8 MiB limit.");
            }

            ms.Write(buffer, 0, read);
        }

        if (total == 0)
        {
            throw new ArgumentException("Audio payload cannot be empty.");
        }

        return ms.ToArray();
    }
}
