using System.Net;
using IntoChat.OS.Modules.AI.Models;

namespace IntoChat.OS.LocalSetup;

// The model Ollama serves for the local profile. Defaults to the canonical local chat role; qwen3.5:2b is the supported smaller override.
public sealed record LocalModelOptions
{
    public static string DefaultModel => KnownModels.Defaults.Device[Role.Chat].Value;
    public const string SmallModel = "qwen3.5:2b";
    public const string DefaultEndpoint = LocalSetupKeys.LocalOllamaEndpoint;

    public string Endpoint { get; init; } = DefaultEndpoint;
    public string Model { get; init; } = DefaultModel;
    public bool EndpointExplicit { get; init; }
    public bool AllowRegistry { get; init; }

    public Uri EndpointUri => new(Endpoint, UriKind.Absolute);

    // Null when valid. A pull only ever targets a loopback Ollama unless --endpoint was given, and only the default
    // Ollama library unless --allow-registry was given (a model name with a registry host pulls from that host).
    public string? Validate()
    {
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return $"Endpoint '{Endpoint}' is not an http(s) URL.";
        }
        if (!EndpointExplicit && !IsLoopback(uri))
        {
            return "Ollama endpoint must be on this machine unless --endpoint is given.";
        }
        if (string.IsNullOrWhiteSpace(Model) || Model.Any(char.IsWhiteSpace))
        {
            return "Model name is empty or contains whitespace.";
        }
        if (!AllowRegistry && HasRegistryHost(Model))
        {
            return $"Model '{Model}' names a registry host; pass --allow-registry to pull from a registry other than the Ollama library.";
        }
        return null;
    }

    private static bool IsLoopback(Uri uri)
        => uri.IsLoopback || (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));

    private static bool HasRegistryHost(string model)
    {
        var slash = model.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0) { return false; }
        var first = model[..slash];
        return first.Contains('.', StringComparison.Ordinal) || first.Contains(':', StringComparison.Ordinal);
    }
}
