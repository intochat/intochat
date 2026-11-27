using DigitalBrain;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Modules.AI.Voice;

internal static class VoiceSettings
{
    internal const string VoicePersonaVariable = "ai.voice.persona";
    private const int MaxVoiceChars = 128;

    public static async Task<string?> ReadVoicePersonaAsync(
        INeuronProxies? proxies,
        BrainId brain,
        CancellationToken ct)
    {
        if (proxies is null || string.IsNullOrWhiteSpace(brain.Value))
        {
            return null;
        }

        try
        {
            var settings = await proxies.Get<IUserSettings>(new NeuronId(brain, "os.settings", "settings")).Read().WaitAsync(ct).ConfigureAwait(false);
            var raw = (settings.Variables ?? [])
                .FirstOrDefault(static v => string.Equals(v.Key, VoicePersonaVariable, StringComparison.Ordinal))
                ?.Value;
            return NormalizeVoicePersona(raw);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            OsConsoleLog.For<TalkEvent>().LogSwallowed(error, "reading ai.voice.persona from user settings");
            return null;
        }
    }

    internal static string? NormalizeVoicePersona(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        if (string.Equals(trimmed, "default", StringComparison.OrdinalIgnoreCase)
            || trimmed.Length > MaxVoiceChars
            || trimmed.Any(char.IsControl))
        {
            return null;
        }

        return trimmed;
    }
}
