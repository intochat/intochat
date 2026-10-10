using DigitalBrain.Framework;
using DigitalBrain.Framework.Modules;

namespace DigitalBrain.Microsoft.Orleans;

public sealed class OrleansOptions : IModuleOptions
{
    // The blob container both grain stores live in when the integration is present.
    public string GrainStateContainer { get; set; } = DigitalBrainNames.GrainStateContainer;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(GrainStateContainer)) { throw new InvalidOperationException("GrainStateContainer is required."); }
    }
}
