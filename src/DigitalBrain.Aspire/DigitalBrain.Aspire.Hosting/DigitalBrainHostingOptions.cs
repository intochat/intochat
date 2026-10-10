using Microsoft.Extensions.Configuration;

namespace DigitalBrain.Aspire.Hosting;

public sealed record DigitalBrainHostingOptions
{
    public const string ServiceIdKey = "Orleans:ServiceId";
    public const string ClusterIdKey = "Orleans:ClusterId";
    public const string GrainStateContainerKey = DigitalBrain.Framework.DigitalBrainNames.GrainStateContainerKey;
    public const string PersistentStorageKey = "DigitalBrain:Hosting:PersistentStorage";
    public const string EnvironmentKey = "DigitalBrain:Hosting:Environment";

    public string? ServiceId { get; init; }
    public string? ClusterId { get; init; }
    // The blob container the default grain storage opens; a revision runs in its own copy.
    public string? GrainStateContainer { get; init; }
    public bool PersistentStorage { get; init; } = true;
    public string? DataVolume { get; init; }
    // Development or Production for every silo host; null leaves each resource's own default.
    public string? Environment { get; init; }

    // A deployment's identity: configuration first, then the name the product calls itself. The cluster id defaults
    // in AddDigitalBrain: a fresh one per Development run (membership rows of a stopped run never block the next
    // run's silos), the service id otherwise.
    public static DigitalBrainHostingOptions Named(IConfiguration configuration, string defaultIdentity)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultIdentity);
        return new()
        {
            ServiceId = configuration[ServiceIdKey] ?? defaultIdentity,
            ClusterId = configuration[ClusterIdKey],
            GrainStateContainer = configuration[DigitalBrain.Framework.DigitalBrainNames.GrainStateContainerKey] ?? DigitalBrain.Framework.DigitalBrainNames.GrainStateContainer,
        };
    }
}