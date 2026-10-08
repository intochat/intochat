using DigitalBrain.Framework.Modules;

namespace DigitalBrain.Machine;

// Off by default: enabling host commands and file writes is a deliberate operator decision.
// Root confines file IO to one directory; empty means the whole machine.
public sealed class MachineOptions : IModuleOptions
{
    public bool Enabled { get; set; }

    public string Root { get; set; } = "";

    public int MaxOutputChars { get; set; } = 20_000;

    public void Validate() { }
}
