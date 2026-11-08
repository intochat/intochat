using DigitalBrain;
using DigitalBrain.Runtime.Synapses;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps;

// check_csharp on synapse source: the silo's SynapseCompiler runs the framework's synapse analyzers (SynapseShape, StateLocality,
// StatusString) with the compile, so the Builder sees what a deploy would refuse.
internal static class SynapseCheck
{
    public static bool IsSynapseSource(IReadOnlyDictionary<string, string> files)
        => files.Values.Any(source => source.Contains("[Synapse", StringComparison.Ordinal));

    // Null when the files are not synapse source; throws when they are but the silo hosts no synapse compiler.
    public static ScriptCompilationCheck? Compile(IServiceProvider services, IReadOnlyDictionary<string, string> files)
    {
        if (!IsSynapseSource(files)) { return null; }
        var compiler = services.GetService<SynapseCompiler>() ?? throw new InvalidOperationException(SynapseStudioNeuron.Unavailable);
        var build = compiler.Compile(new SynapseSource("check", files, null));
        return new ScriptCompilationCheck(build.Success, [.. build.Diagnostics.Select(d => new ScriptCompilationDiagnostic(d.File, d.Id, d.Line, d.Message))]);
    }
}

// A unit lands through the studio; its install outcome, not the deploy, says whether it is live.
internal static class UnitDeploy
{
    // The install outcome of alias in result: its own entry, the only entry, or Unchanged when the host installed nothing (a unit
    // with no state alias, or a brain with no installs neuron).
    public static DigitalBrain.InstallOutcome Install(UnitDeployResult result, string alias)
        => result.Installs.TryGetValue(alias, out var own) ? own
            : result.Installs.Count == 1 ? result.Installs.Values.Single()
            : new DigitalBrain.InstallOutcome(DigitalBrain.InstallStatus.Unchanged, "", null);

    public static bool IsLive(DigitalBrain.InstallStatus status) => status is DigitalBrain.InstallStatus.Installed or DigitalBrain.InstallStatus.Updated or DigitalBrain.InstallStatus.Unchanged;

    // Deploys the build's files and ends the attempt by the install: Live, Building while the needs wait for the person, else Failed
    // with the reason. Returns the failure, or null.
    public static async Task<string?> Complete(IApp app, Guid attempt, ISynapseStudio studio, string alias, IReadOnlyDictionary<string, string> files,
        IReadOnlyDictionary<string, string> bindings)
    {
        var result = await studio.DeployFiles(alias, files, bindings);
        if (!result.Ok) { await app.CompleteBuild(attempt, null, result.Text); return result.Text; }
        var install = Install(result, alias);
        if (install.Status is DigitalBrain.InstallStatus.Refused or DigitalBrain.InstallStatus.Denied)
        {
            var failure = $"{alias} was not installed: {install.Reason ?? install.Status.ToString()}";
            await app.CompleteBuild(attempt, null, failure);
            return failure;
        }
        await app.CompleteUnitBuild(attempt, result.Hash ?? "", install.Status, install.Sentence);
        return null;
    }
}
