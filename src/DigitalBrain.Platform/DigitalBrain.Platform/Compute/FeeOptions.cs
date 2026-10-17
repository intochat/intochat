namespace DigitalBrain.Platform.Compute;

// The platform's cut on top of list price for work billed through an integration.
public sealed class FeeOptions
{
    public const string Key = "DigitalBrain:Platform:Fees";

    public int PlatformPercent { get; set; } = 25;
}
