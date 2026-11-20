using Microsoft.Extensions.Configuration;
using IntoChat.OS.Host.Configuration;

namespace IntoChat.OS.Aspire.Hosting;

public sealed record IntoChatOSHostingOptions
{
    public const string ServiceIdKey = "Orleans:ServiceId";
    public const string ClusterIdKey = "Orleans:ClusterId";
    public const string GrainStateContainerKey = IntoChatOSNames.GrainStateContainerKey;
    // The Ollama model the local profile serves instead of the default (qwen3.5:4b).
    public const string LocalModelKey = IntoChatOSNames.LocalModelKey;
    public const string EnvironmentKey = "IntoChatOS:Hosting:Environment";

    public string? ServiceId { get; init; }
    public string? ClusterId { get; init; }
    // The blob container the default grain storage opens; a revision runs in its own copy.
    public string? GrainStateContainer { get; init; }
    public string? DataVolume { get; init; }
    // Development or Production for every silo host; null leaves each resource's own default.
    public string? Environment { get; init; }

    // A deployment's identity: configuration first, then the name the product calls itself. The cluster id defaults
    // in AddIntoChatOS: a fresh one per Development run (membership rows of a stopped run never block the next
    // run's silos), the service id otherwise.
    public static IntoChatOSHostingOptions Named(IConfiguration configuration, string defaultIdentity)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultIdentity);
        return new()
        {
            ServiceId = configuration[ServiceIdKey] ?? defaultIdentity,
            ClusterId = configuration[ClusterIdKey],
            GrainStateContainer = configuration[IntoChatOSNames.GrainStateContainerKey] ?? IntoChatOSNames.GrainStateContainer,
        };
    }
}
