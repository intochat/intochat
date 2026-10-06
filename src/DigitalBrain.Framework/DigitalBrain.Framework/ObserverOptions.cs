namespace DigitalBrain.Framework;

public sealed class ObserverOptions
{
    public TimeSpan Lease { get; set; } = TimeSpan.FromMinutes(5);
}
