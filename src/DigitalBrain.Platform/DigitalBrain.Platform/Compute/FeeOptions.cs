namespace DigitalBrain.Platform.Compute;

// The platform's cut on top of list price for work billed through an integration.
public sealed class FeeOptions
{
    public int PlatformPercent { get; set; } = 25;
}