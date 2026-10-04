namespace DigitalBrain.Aspire.Hosting;

public sealed class DigitalBrainHostingOptions
{
    public string? ServiceId { get; init; }
    public string? ClusterId { get; init; }
    public bool UseAzureStorage { get; init; }
    // Azurite lifetime. Product runs persist; a test host passes false so deployments do not share a volume.
    public bool PersistentStorage { get; init; } = true;
    public string? DataVolume { get; init; }
}
