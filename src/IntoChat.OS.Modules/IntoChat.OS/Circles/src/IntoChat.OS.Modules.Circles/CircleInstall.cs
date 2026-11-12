using DigitalBrain.Brains;
using DigitalBrain.Neurons;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Runtime;

namespace IntoChat.OS.Modules.Circles;

// Fork and bring install the exact release the person selected through the one install path. They carry the consent
// of no requirements: a release that asks for data, connections or secrets is refused here, and the Circle UI opens
// its Apps detail, where the person sees what it asks for and installs it.
internal static class CircleInstall
{
    public static async Task<AppSnapshot> InstallExact(this AppInstaller installer, INeuronProxies brain, AppPublicationRef publication, string targetName, Guid operationId)
    {
        ArgumentNullException.ThrowIfNull(publication);
        var request = new InstallRequest(new(publication.Publisher, publication.Name, publication.Version, publication.Digest),
            AppConsent.Of(null), operationId, targetName);
        var outcome = await installer.Install(request, CancellationToken.None, publication.Source);
        if (outcome.Status != InstallStatus.Ok)
        { throw new InvalidOperationException(outcome.Status == InstallStatus.NeedsConsent ? AppInstaller.ReviewInApps : outcome.Message); }
        return await brain.Get<IApp>(IApp.Key(BrainScope.CurrentId(), targetName)).Read();
    }
}
