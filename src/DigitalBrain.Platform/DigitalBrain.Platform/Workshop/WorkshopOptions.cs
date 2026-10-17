namespace DigitalBrain.Platform.Workshop;

// The OS repository root the Workshop may edit. Empty by default: a host must opt in explicitly.
public sealed class WorkshopOptions
{
    public string Root { get; set; } = "";
}
