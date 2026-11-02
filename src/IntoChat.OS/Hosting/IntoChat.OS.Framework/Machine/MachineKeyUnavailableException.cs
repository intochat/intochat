// os-only: os machine key custody failure; not a framework concern.
namespace IntoChat.OS.Framework.Machine;

// Fixed text: the message never carries key material or custody detail (T-10-09).
public sealed class MachineKeyUnavailableException : Exception
{
    public const string FixedMessage = "machine key unavailable";

    public MachineKeyUnavailableException() : base(FixedMessage) { }

    public MachineKeyUnavailableException(Exception innerException) : base(FixedMessage, innerException) { }

    public MachineKeyUnavailableException(string message) : base(message) { }

    public MachineKeyUnavailableException(string message, Exception innerException) : base(message, innerException) { }
}
