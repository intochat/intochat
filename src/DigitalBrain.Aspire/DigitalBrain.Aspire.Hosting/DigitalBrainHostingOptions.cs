namespace DigitalBrain.Aspire.Hosting;

public sealed class DigitalBrainHostingOptions
{
    // External override channels: a test or deployment may force these on a composed AppHost
    // through its configuration without touching code.
    public const string ServiceIdKey = "Orleans:ServiceId";
    public const string ClusterIdKey = "Orleans:ClusterId";
    // Azurite lifetime. Product runs persist; a test host passes false so deployments do not share a volume.
    public const string PersistentStorageKey = "DigitalBrain:Hosting:PersistentStorage";
    public const string EnvironmentKey = "DigitalBrain:Hosting:Environment";

    public string? ServiceId { get; init; }
    public string? ClusterId { get; init; }
    public bool UseAzureStorage { get; init; }
    public bool PersistentStorage { get; init; } = true;
    public string? DataVolume { get; init; }
    // Development or Production for every silo host; null leaves each resource's own default.
    public string? Environment { get; init; }
}
