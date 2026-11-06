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

// A headless app's build emits a synapse unit through the studio instead of an app program.
internal static class HeadlessUnit
{
    // The live build hash when the unit deployed; else Failure is the studio's reason.
    public static async Task<(string? Hash, string? Failure)> Deploy(ISynapseStudio studio, string alias, IReadOnlyDictionary<string, string> files)
    {
        var result = await studio.DeployFiles(alias, files);
        if (!result.Ok) { return (null, result.Text); }
        var live = (await studio.List()).FirstOrDefault(unit => unit.SourceKey == alias);
        return live is null ? (null, $"{alias} deployed but is not live.") : (live.Hash, null);
    }

    // Deploys the build's files and ends the attempt: Live with the unit's hash, or Failed with the reason.
    public static async Task<string?> Complete(IApp app, Guid attempt, ISynapseStudio studio, string alias, IReadOnlyDictionary<string, string> files)
    {
        var (hash, failure) = await Deploy(studio, alias, files);
        if (hash is not null) { await app.CompleteHeadlessBuild(attempt, hash); }
        else { await app.CompleteBuild(attempt, null, failure); }
        return failure;
    }
}
