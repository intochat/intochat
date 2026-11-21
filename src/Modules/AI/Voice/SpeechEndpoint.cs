using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;

namespace IntoChat.OS.Modules.AI.Voice;

internal static class SpeechEndpoint
{
    private const int MaxTextChars = 4_096;
    private const int MaxVoiceChars = 128;
    private const int DefaultPcmSampleRate = 24_000;

    public static async Task<IResult> HandleAsync(
        HttpContext http,
        IVoiceClients clients,
        CancellationToken ct)
    {
        if (AIModule.StampedCaller(http) is not { } caller)
        {
            return Results.Unauthorized();
        }

        if (!AIModule.IsDirectPersonCaller(caller))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        SpeechRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync(
                http.Request.Body,
                TalkJson.Default.SpeechRequest,
                ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return Results.Problem("Supply a JSON body with a 'text' string.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > MaxTextChars)
        {
            return Results.Problem(
                $"Text must contain between 1 and {MaxTextChars} characters.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        string? voiceId = null;
        if (!string.IsNullOrWhiteSpace(request.Voice))
        {
            voiceId = request.Voice.Trim();
            if (voiceId.Length > MaxVoiceChars || voiceId.Any(char.IsControl))
            {
                return Results.Problem("Voice name must contain at most 128 printable characters.", statusCode: StatusCodes.Status400BadRequest);
            }
        }

        var resolved = await clients.Speak(caller.Brain.Value, ct).ConfigureAwait(false);
        if (resolved.Client is null)
        {
            return Results.Problem(
                resolved.Unavailable ?? "Speech synthesis is unavailable.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        using var client = resolved.Client;
        try
        {
            var options = new TextToSpeechOptions
            {
                ModelId = resolved.Model?.Value,
                VoiceId = voiceId,
            };
            var response = await client.GetAudioAsync(request.Text, options, ct).ConfigureAwait(false);
            var audio = response.Contents.OfType<DataContent>().FirstOrDefault(static c => !c.Data.IsEmpty);
            if (audio is null)
            {
                return Results.Problem("Speech synthesis returned no audio.", statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (audio.MediaType is { Length: > 0 } mt && mt.StartsWith("audio/pcm", StringComparison.OrdinalIgnoreCase))
            {
                var rate = ParsePcmRate(mt);
                var wav = Pcm16.Wav(audio.Data.Span, rate);
                return Results.File(wav, "audio/wav");
            }

            return Results.File(audio.Data.ToArray(), audio.MediaType ?? "audio/mpeg");
        }
        catch (NotSupportedException error)
        {
            return Results.Problem(error.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (ArgumentException error)
        {
            return Results.Problem(error.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    internal static int ParsePcmRate(string mediaType)
    {
        foreach (var part in mediaType.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("rate=", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(part[5..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                && parsed > 0)
            {
                return parsed;
            }
        }

        return DefaultPcmSampleRate;
    }
}
