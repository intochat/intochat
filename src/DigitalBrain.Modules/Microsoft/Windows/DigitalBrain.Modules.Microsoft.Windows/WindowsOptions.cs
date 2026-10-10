using DigitalBrain.Framework.Modules;

namespace DigitalBrain.Microsoft.Windows;

// MachineIdFile empty = %LOCALAPPDATA%\DigitalBrain\machine-id; tests point it at a temp file.
public sealed class WindowsOptions : IModuleOptions
{
    public string MachineIdFile { get; set; } = "";

    public void Validate() { }
}
