using DigitalBrain.Framework;
using DigitalBrain.Framework.Modules;

namespace DigitalBrain.Microsoft.Orleans;

public sealed class OrleansOptions : IModuleOptions
{
    // The blob container both grain stores live in when the integration is present.
    public string GrainStateContainer { get; set; } = DigitalBrainNames.GrainStateContainer;

    // A hosting choice: the AppHost provisions Azurite and projects it into the orleans integration.
    public AzuriteOptions Azurite { get; set; } = new();

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(GrainStateContainer)) { throw new InvalidOperationException("GrainStateContainer is required."); }
        ArgumentNullException.ThrowIfNull(Azurite);
    }
}

public sealed class AzuriteOptions
{
    public bool Enabled { get; set; }
    public bool PersistentStorage { get; set; } = true;
    public string? DataVolume { get; set; }
}
