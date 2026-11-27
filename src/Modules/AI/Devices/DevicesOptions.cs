using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DigitalBrain.Modules.Options;
using IntoChat.OS.Host.Configuration;

namespace IntoChat.OS.Modules.Devices;

public sealed class OllamaRuntimeOptions
{
    public bool Enabled { get; set; } = true;
    public string Endpoint { get; set; } = IntoChatOSNames.LocalOllamaEndpoint;
    public int ProbeTimeoutMs { get; set; } = 1_500;
}

public sealed class FoundryLocalRuntimeOptions
{
    public bool Enabled { get; set; } = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
    public string? CacheDirectory { get; set; }
}

public sealed class SherpaOnnxRuntimeOptions
{
    public bool Enabled { get; set; } = true;
    public string? ModelDirectory { get; set; }
}

public sealed class AttachedRuntimeOptions
{
    public string Url { get; set; } = string.Empty;
}

public sealed class ComputeCapabilityOptions
{
    public ComputeConsentMode ConsentMode { get; set; } = ComputeConsentMode.ExplicitToggle;

    public bool AllowClientCodeSandbox { get; set; } = ClientComputeConsentPolicy.DefaultAllowClientCodeSandboxForCurrentPlatform();

    /// <summary>
    /// Fail-closed consent for Plane 2 Connected PC self-improving engineering (strictly separate from
    /// <see cref="AllowClientCodeSandbox"/>). Defaults to <see langword="false"/> everywhere unless explicitly enabled.
    /// </summary>
    public bool AllowConnectedEngineering { get; set; }

    public bool SandboxEnabled { get; set; } = true;

    public int MaxConcurrency { get; set; } = 2;

    public int HeartbeatIntervalSeconds { get; set; } = 15;

    public int HeartbeatTtlSeconds { get; set; } = 45;

    public int AvailabilityTimeoutMs { get; set; } = 400;

    public bool IsClientCodeSandboxConsented => IsClientCodeSandboxConsentedWith(AllowClientCodeSandbox);

    public bool IsClientCodeSandboxConsentedWith(bool allowClientCodeSandbox)
        => SandboxEnabled && ConsentMode switch
        {
            ComputeConsentMode.ExplicitToggle => allowClientCodeSandbox,
            ComputeConsentMode.AutoAll => true,
            _ => false,
        };
}

public sealed partial class DevicesOptions : IModuleOptions
{
    [GeneratedRegex("^[a-z0-9-]{1,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex AttachNamePattern();

    public OllamaRuntimeOptions Ollama { get; set; } = new();

    public FoundryLocalRuntimeOptions FoundryLocal { get; set; } = new();

    public SherpaOnnxRuntimeOptions SherpaOnnx { get; set; } = new();

    public ComputeCapabilityOptions Compute { get; set; } = new();

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
        if (Ollama.ProbeTimeoutMs is < 100 or > 60_000)
        {
            throw new ArgumentOutOfRangeException(nameof(Ollama), Ollama.ProbeTimeoutMs, "Devices Ollama.ProbeTimeoutMs must be between 100 and 60000 ms.");
        }

        if (FoundryLocal is null)
        {
            throw new ArgumentException("Devices FoundryLocal options cannot be null.", nameof(FoundryLocal));
        }

        if (SherpaOnnx is null)
        {
            throw new ArgumentException("Devices SherpaOnnx options cannot be null.", nameof(SherpaOnnx));
        }

        if (Compute is null)
        {
            throw new ArgumentException("Devices Compute options cannot be null.", nameof(Compute));
        }
        if (Compute.MaxConcurrency is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(Compute), Compute.MaxConcurrency, "Devices Compute.MaxConcurrency must be between 1 and 64.");
        }
        if (Compute.HeartbeatIntervalSeconds is < 1 or > 300)
        {
            throw new ArgumentOutOfRangeException(nameof(Compute), Compute.HeartbeatIntervalSeconds, "Devices Compute.HeartbeatIntervalSeconds must be between 1 and 300.");
        }
        if (Compute.HeartbeatTtlSeconds < Compute.HeartbeatIntervalSeconds || Compute.HeartbeatTtlSeconds > 3600)
        {
            throw new ArgumentOutOfRangeException(nameof(Compute), Compute.HeartbeatTtlSeconds, "Devices Compute.HeartbeatTtlSeconds must be >= HeartbeatIntervalSeconds and <= 3600.");
        }
        if (Compute.AvailabilityTimeoutMs is < 50 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(Compute), Compute.AvailabilityTimeoutMs, "Devices Compute.AvailabilityTimeoutMs must be between 50 and 500 ms.");
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
