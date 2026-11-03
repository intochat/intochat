using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps;

internal sealed partial class AppNeuron
{
    private void ValidateInstallation(AppBundle bundle, AppOrigin? origin, AppInstallReceipt installation)
    {
        var reference = installation.Publication;
        if (!CallerContextStamper.TryGet(out var caller) || caller.AccountId != installation.AccountId
            || BrainScope.CurrentId() != BrainId())
        { throw new UnauthorizedAccessException("An installation belongs to its initiating account and destination brain."); }
        if (installation.OperationId == Guid.Empty || installation.TargetName != Name()
            || (origin is null ? null : origin with { Consented = null }) != new AppOrigin(reference.Publisher, reference.Source, reference.Name, reference.Version, reference.Digest, caller.AccountId)
            || bundle.Version != reference.Version || AppPublicationDigest.Of(bundle) != reference.Digest)
        { throw new InvalidOperationException("The installation receipt does not match this publication."); }
    }

    // Phase F: an installed app is a principal of its brain. Its membership (role App, under the brain owner's account)
    // exists from the moment the copy is created or lands until it is deleted. A host without the identity
    // authority, or a brain nobody owns yet, records none; the app proxy still stamps the app principal.
    private async Task EnsureAppPrincipal()
    {
        if (ServiceProvider.GetService<ICapabilityBroker>() is not { } broker || Snapshot.Manifest is not { } manifest) { return; }
        var brain = BrainId();
        IIdentity directory;
        try { directory = await broker.Identity(); }
        catch (InvalidOperationException) { return; }
        if (await directory.OwnerOfBrain(brain) is not { } account) { return; }
        // Host install runs with the caller removed. Membership changes require the brain owner.
        // AppId stays empty: the directory accepts the owner, and a platform sentinel is not the owner.
        using var owner = RuntimeCallerContext.Stamp(CapabilityCallers.Of(account.OwnerPrincipalId, account.AccountId, brain));
        await directory.EnsureMember(new Member
        {
            PrincipalId = AppPrincipal.Id(Key),
            AccountId = account.AccountId,
            BrainId = brain,
            Role = MemberRole.App,
            DisplayName = manifest.Title,
            JoinedAt = clock.GetUtcNow(),
        });
    }

    private async Task RemoveAppPrincipal()
    {
        if (ServiceProvider.GetService<ICapabilityBroker>() is not { } broker) { return; }
        var brain = BrainId();
        var principal = AppPrincipal.Id(Key);
        IIdentity directory;
        try { directory = await broker.Identity(); }
        catch (InvalidOperationException) { return; }
        if (await directory.OwnerOfBrain(brain) is not { } account) { return; }
        if (await directory.Membership(brain, principal) is not { Role: MemberRole.App }) { return; }
        using var owner = RuntimeCallerContext.Stamp(CapabilityCallers.Of(account.OwnerPrincipalId, account.AccountId, brain));
        await directory.RemovePrincipalMember(brain, principal);
    }
}
