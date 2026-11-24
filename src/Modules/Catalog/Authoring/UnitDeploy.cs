using DigitalBrain;
using DigitalBrain.Units;

namespace IntoChat.OS.Modules.Apps;

// A unit lands through the studio; its install outcome, not the deploy, says whether it is live.
internal static class UnitDeploy
{
    // The install outcome of alias in result: its own entry, the only entry, or Unchanged when the host installed nothing (a unit
    // with no state alias, or a brain with no installs neuron).
    public static DigitalBrain.Units.InstallOutcome Install(UnitDeployResult result, string alias)
        => result.Installs.TryGetValue(alias, out var own) ? own
            : result.Installs.Count == 1 ? result.Installs.Values.Single()
            : new DigitalBrain.Units.InstallOutcome(DigitalBrain.Units.InstallStatus.Unchanged, "", null);

    public static bool IsLive(DigitalBrain.Units.InstallStatus status) => status is DigitalBrain.Units.InstallStatus.Installed or DigitalBrain.Units.InstallStatus.Updated or DigitalBrain.Units.InstallStatus.Unchanged;

    // Deploys the build's files and ends the attempt by the install: Live, Building while the needs wait for the person, else Failed
    // with the reason. Returns the failure, or null.
    public static async Task<string?> Complete(IApp app, Guid attempt, ISynapseStudio studio, string alias, IReadOnlyDictionary<string, string> files,
        IReadOnlyDictionary<string, string> bindings)
    {
        var result = await studio.DeployFiles(alias, files, bindings);
        if (!result.Ok) { await app.CompleteBuild(attempt, null, result.Text); return result.Text; }
        var install = Install(result, alias);
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
