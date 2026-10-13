using DigitalBrain.Framework;

namespace DigitalBrain.Apps;

internal sealed partial class AppNeuron
{
    private void ValidateInstallation(AppBundle bundle, AppOrigin? origin, AppInstallReceipt installation)
    {
        var reference = installation.Publication;
        if (!CallerContextStamper.TryGet(out var caller) || caller.AccountId != installation.AccountId
            || BrainScope.CurrentId() != BrainId())
        { throw new UnauthorizedAccessException("An installation belongs to its initiating account and destination brain."); }
        if (installation.OperationId == Guid.Empty || installation.TargetName != Name()
            || origin != new AppOrigin(reference.Publisher, reference.Source, reference.Name, reference.Version, reference.Digest, caller.AccountId)
            || bundle.Version != reference.Version || AppPublicationDigest.Of(bundle) != reference.Digest)
        { throw new InvalidOperationException("The installation receipt does not match this publication."); }
    }
}
