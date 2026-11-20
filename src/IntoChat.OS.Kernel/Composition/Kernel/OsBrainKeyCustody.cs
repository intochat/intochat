using DigitalBrain.Identity.Local.Custody;
namespace IntoChat.OS.Kernel.Composition.Kernel;

// The os master key wraps brain journal keys. Registered before the runtime's BrainLog so it replaces the configuration default.
public sealed class OsBrainKeyCustody(DigitalBrain.Identity.Local.Custody.IMasterKeyCustody custody) : DigitalBrain.Runtime.Orleans.IBrainKeyCustody
{
    public byte[] Wrap(string accountId, string context, byte[] secret) => custody.Wrap(accountId, context, secret);

    public byte[] Unwrap(string accountId, string context, byte[] wrapped) => custody.Unwrap(accountId, context, wrapped);
}
