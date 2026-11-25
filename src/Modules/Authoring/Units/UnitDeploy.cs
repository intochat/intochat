using DigitalBrain;
using DigitalBrain.Units;

namespace IntoChat.OS.Modules.Apps;

internal static class UnitDeploy
{
    // Deploys the build's files and ends the attempt by the install: Live, Building while the needs wait for the person, else Failed
    // with the reason. Returns the failure, or null.
    public static async Task<string?> Complete(IApp app, Guid attempt, ISynapseStudio studio, string alias, IReadOnlyDictionary<string, string> files,
        IReadOnlyDictionary<string, string> bindings)
    {
        var result = await studio.DeployFiles(alias, files, bindings);
        if (!result.Ok) { await app.CompleteBuild(attempt, null, result.Text); return result.Text; }
        var install = result.InstallOf(alias);
        if (install.Status is DigitalBrain.Units.InstallStatus.Refused or DigitalBrain.Units.InstallStatus.Denied)
        {
            var failure = $"{alias} was not installed: {install.Reason ?? install.Status.ToString()}";
            await app.CompleteBuild(attempt, null, failure);
            return failure;
        }
        await app.CompleteUnitBuild(attempt, result.Hash ?? "", install.Status, install.Sentence);
        return null;
    }
}
