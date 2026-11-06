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
    // Null when the unit deployed, else the studio's reason.
    public static async Task<string?> Deploy(ISynapseStudio studio, string alias, IReadOnlyDictionary<string, string> files)
    {
        var source = string.Join("\n", files.OrderBy(file => file.Key, StringComparer.Ordinal).Select(file => file.Value));
        var result = await studio.Deploy(alias, source);
        return result.Text.StartsWith(alias + " is ", StringComparison.Ordinal) ? null : result.Text;
    }
}
