using DigitalBrain.Framework.Parts;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Platform.Sequencer;
using DigitalBrain.Platform.Trust;

namespace DigitalBrain.Platform.Parts;

// Q11/Q17/R128: which machine may hold a part, and where a circle part prefers to run. An owner's core stays with a
// machine that serves its owner; a circle part runs on a machine contributed to that circle (R128), preferring the
// current operator's contributed machine. A Cloud machine holds no private part (Q24). The trust side answers
// machines and accounts, so no module contract is needed.
internal sealed class LeasePreference(MachineIdentity machine, PartsOptions options, TimeProvider time, IIdentityClient identity, IKeyringClient keyring)
{
    public async Task<bool> MayHold(PartId part, string brainId)
    {
        ArgumentNullException.ThrowIfNull(part);
        if (options.Role == MachineRole.Cloud) { return false; }
        if (await identity.KindOf(brainId) == BrainKind.Circle)
        { return (await identity.MachinesOf(brainId)).Contains(machine.MachineId, StringComparer.Ordinal); }
        // An owner's brain: only a machine that serves the owner may hold its parts. Before the owner is recorded
        // (a brain being born) any own machine may open it.
        if (await keyring.OwnerOf(brainId) is not { } owner) { return true; }
        return (await identity.AccountsOf(machine.MachineId)).Contains(owner, StringComparer.Ordinal);
    }

    public async Task<string?> Preferred(PartId part, string brainId, string? operatorAccount)
    {
        ArgumentNullException.ThrowIfNull(part);
        if (await identity.KindOf(brainId) != BrainKind.Circle) { return null; }
        var machines = await identity.MachinesOf(brainId);
        if (operatorAccount is { Length: > 0 })
        {
            foreach (var candidate in machines)
            {
                if ((await identity.AccountsOf(candidate)).Contains(operatorAccount, StringComparer.Ordinal)) { return candidate; }
            }
        }
        return machines.FirstOrDefault();
    }

    // Q17: a holder that is not the preferred machine returns the part once it has been quiet for QuietFor, so the
    // lease follows the operator back to their own machine at the next quiet moment. A machine is preferred only while
    // it is contributed to the circle (R128), so a preferred machine is online by construction.
    public async Task<bool> ShouldReturn(PartLease lease, string? operatorAccount, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        cancellationToken.ThrowIfCancellationRequested();
        var preferred = await Preferred(lease.Part, lease.Part.BrainId, operatorAccount);
        if (preferred is null || preferred == machine.MachineId) { return false; }
        return time.GetUtcNow() - lease.LastActivity >= options.QuietFor;
    }
}
