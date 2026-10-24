using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Platform.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Apps;

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
        if (ServiceProvider.GetService<IIdentityClient>() is not { } identity || Snapshot.Manifest is not { } manifest) { return; }
        var brain = BrainId();
        if (await identity.OwnerOfBrain(brain) is not { } account) { return; }
        await identity.EnsureMember(new Member
        {
            PrincipalId = AppPrincipal.Id(this.GetPrimaryKeyString()),
            AccountId = account.AccountId,
            BrainId = brain,
            Role = MemberRole.App,
            DisplayName = manifest.Title,
            JoinedAt = clock.GetUtcNow(),
        });
    }

    private async Task RemoveAppPrincipal()
    {
        if (ServiceProvider.GetService<IIdentityClient>() is not { } identity) { return; }
        var brain = BrainId();
        var principal = AppPrincipal.Id(this.GetPrimaryKeyString());
        if (await identity.Membership(brain, principal) is { Role: MemberRole.App })
        { await identity.RemovePrincipalMember(brain, principal); }
    }
}
