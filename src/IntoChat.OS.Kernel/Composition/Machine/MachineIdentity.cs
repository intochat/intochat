using DigitalBrain.Modules.Configuration;
using IntoChat.OS.Framework.Machine;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Kernel.Composition.Machine;

// The install's stable id: created once, then read, at the same %LOCALAPPDATA%\IntoChatOS\machine-id default and
// WindowsOptions.MachineIdFile option as before the runtime contract. A garbled file is an error, never silently
// replaced, because replacing it would orphan the machine's connection.
internal sealed class MachineIdentity : IMachineIdentity
{
    private readonly Lazy<string> _id;

    public MachineIdentity(IConfiguration? configuration = null)
    {
        var configured = configuration?[$"{ModuleConfiguration.ModuleOptionsSection("windows")}:MachineIdFile"]
            ?? configuration?[MachineIdFile.ConfigurationKey];
        File = MachineIdFile.Resolve(configured);
        _id = new(() => MachineIdFile.ReadOrCreate(File));
    }

    public string File { get; }

    public string Id => _id.Value;

    public string DisplayName => Environment.MachineName;
}
