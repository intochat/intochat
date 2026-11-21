using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DigitalBrain.Modules.Options;
using IntoChat.OS.Host.Configuration;

namespace IntoChat.OS.Modules.Devices;

public sealed class OllamaRuntimeOptions
{
    public bool Enabled { get; set; } = true;
    public string Endpoint { get; set; } = IntoChatOSNames.LocalOllamaEndpoint;
}

public sealed class FoundryLocalRuntimeOptions
{
    public bool Enabled { get; set; } = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
    public string? CacheDirectory { get; set; }
}

public sealed class AttachedRuntimeOptions
{
    public string Url { get; set; } = string.Empty;
}

public sealed partial class DevicesOptions : IModuleOptions
{
    [GeneratedRegex("^[a-z0-9-]{1,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex AttachNamePattern();

    public OllamaRuntimeOptions Ollama { get; set; } = new();

    public FoundryLocalRuntimeOptions FoundryLocal { get; set; } = new();

    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<string, AttachedRuntimeOptions> Attach { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Prefetch { get; set; } = [];

    public void Validate()
    {
        if (Ollama is null)
        {
            throw new ArgumentException("Devices Ollama options cannot be null.", nameof(Ollama));
        }
        if (Ollama.Enabled || !string.IsNullOrWhiteSpace(Ollama.Endpoint))
        {
            ValidateHttpUrl(Ollama.Endpoint, "Ollama.Endpoint");
        }

        if (FoundryLocal is null)
        {
            throw new ArgumentException("Devices FoundryLocal options cannot be null.", nameof(FoundryLocal));
        }

        foreach (var (name, entry) in Attach)
        {
            if (string.IsNullOrWhiteSpace(name) || !AttachNamePattern().IsMatch(name))
            {
                throw new ArgumentException($"Attached runtime name '{name}' must match [a-z0-9-]{{1,32}}.");
            }
            if (entry is null)
            {
                throw new ArgumentException($"Attached runtime '{name}' options cannot be null.");
            }
            ValidateHttpUrl(entry.Url, $"Attach['{name}'].Url");
        }
    }

    internal static Uri ValidateHttpUrl(string? raw, string label)
    {
        if (string.IsNullOrWhiteSpace(raw)
            || !Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException($"{label} ('{raw}') must be an absolute http(s) URL without credentials.");
        }
        return uri;
    }
}
