// os-only: os machine enrollment identity (D-11); not a framework concern.
namespace IntoChat.OS.Framework.Machine;

// macOS, Linux and container identity: the same GUID file logic the Windows module uses, at the per-OS default path.
public sealed class FileMachineIdentity : IMachineIdentity
{
    private readonly Lazy<string> _id;

    public FileMachineIdentity(string? configuredFile = null)
    {
        File = MachineIdFile.Resolve(configuredFile);
        _id = new(() => MachineIdFile.ReadOrCreate(File));
    }

    public string File { get; }

    public string Id => _id.Value;

    public string DisplayName => Environment.MachineName;
}
