using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps.Tests;

// The person's install flow through the one install path: ask, see what the app needs, accept exactly that.
internal static class Installs
{
    public static async Task<AppSnapshot> InstallLatest(this ITestBrain brain, string publisher, string name, CancellationToken ct)
        => await Accepting(brain, new(new(publisher, name), "", Guid.Empty), null, ct);

    public static async Task<AppSnapshot> InstallRelease(this ITestBrain brain, AppPublicationRef publication, string name, Guid operationId, CancellationToken ct)
        => await Accepting(brain, new(new(publication.Publisher, publication.Name, publication.Version, publication.Digest), "", operationId, name), publication.Source, ct);

    public static async Task<AppSnapshot> UpdateApp(this ITestBrain brain, string name, CancellationToken ct)
    {
        var outcome = await Installer(brain).Update(new(name), ct);
        if (outcome.Status == InstallStatus.NeedsConsent)
        { throw new InvalidOperationException("The update asks for more: " + outcome.Message); }
        return await Read(brain, outcome, ct);
    }

    private static async Task<AppSnapshot> Accepting(ITestBrain brain, InstallRequest request, AppSource.Brain? source, CancellationToken ct)
    {
        var outcome = await Installer(brain).Install(request, ct, source);
        if (outcome.Status == InstallStatus.NeedsConsent)
        { outcome = await Installer(brain).Install(request with { Consent = AppConsent.Of(outcome.Requires) }, ct, source); }
        return await Read(brain, outcome, ct);
    }

    private static async Task<AppSnapshot> Read(ITestBrain brain, InstallOutcome outcome, CancellationToken ct)
        => outcome.Status == InstallStatus.Ok
            ? await brain.Get<IApp>(IApp.Key(BrainScope.CurrentId(), outcome.InstalledName!)).Read().WaitAsync(ct)
            : throw new InvalidOperationException(outcome.Message);

    private static AppInstaller Installer(ITestBrain brain) => brain.Services.GetRequiredService<AppInstaller>();
}
