using DigitalBrain.Enforcement;
using DigitalBrain.Kernel.Enforcement;
using DigitalBrain.Types;
using DigitalBrain.Kernel.Platform.Integrations.Accounts;
using DigitalBrain.Kernel.Platform.Secrets;
using DigitalBrain.Kernel.Types;
using Microsoft.Extensions.Logging;

namespace DigitalBrain.Platform.Integrations.Accounts;

// Resolves only for this operation; the caller never receives credential bytes or an elevated stamp.
internal sealed class ConnectionCredentialProbe(IGrainFactory grains, IAccountProbe probe, ILogger<ConnectionCredentialProbe> logger)
{
    public async Task<AccountProbeResult> ProbeAsync(string source, SecretRef credential, CallerContext actor, CancellationToken ct)
    {
        if (actor.Kind == CallerKind.App || credential.OwnerOf() != actor.PrincipalId)
        { throw new UnauthorizedAccessException("Only the credential owner can request a connection probe."); }
        logger.LogInformation("Connection probe requested by {Actor} in {Account}/{Brain} for {Source}.", actor.PrincipalId, actor.AccountId, actor.BrainId, source);
        string value;
        try
        {
            value = await grains.GetGrain<ISecrets>(credential.OwnerOf()).Resolve(actor with
            { Kind = CallerKind.Platform, StampedBy = TrustedEdge.Platform, AppId = AccountNames.NeuronType }, credential, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        { return AccountProbeResult.Failing("The stored credential could not be resolved."); }
        try { return await probe.ProbeAsync(source, value, ct); }
        catch (Exception) when (!ct.IsCancellationRequested)
        { return AccountProbeResult.Failing("The read-only probe failed."); }
    }
}
