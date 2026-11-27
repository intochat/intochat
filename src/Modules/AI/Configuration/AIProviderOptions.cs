using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Modules.AI;

public class AIProviderOptions
{
    // For key-based providers this only seeds the provider registration's Endpoint at startup.
    public string? Endpoint { get; set; }
}

public sealed class AzureFoundryOptions : AIProviderOptions
{
    // Keyless Entra ID (DefaultAzureCredential) is the default when no explicit ApiKey is configured.
    public bool UseEntraId { get; set; } = true;
}
