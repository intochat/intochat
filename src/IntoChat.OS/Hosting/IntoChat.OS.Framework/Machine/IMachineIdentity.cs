// os-only: os machine enrollment identity (D-11); not a framework concern.
namespace IntoChat.OS.Framework.Machine;

// The install's stable id on every OS (D-11). Created once, then read; never silently replaced.
public interface IMachineIdentity
{
    string Id { get; }

    string DisplayName { get; }
}
