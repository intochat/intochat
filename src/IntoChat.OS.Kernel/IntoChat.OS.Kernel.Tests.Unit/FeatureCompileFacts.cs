using IntoChat.OS.Apps;
using IntoChat.OS.Framework;
using IntoChat.OS.Microsoft.CSharp;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Kernel.Tests.Unit;

// Boot compiles every shipped feature only when a real sandbox is present; this holds the features
// folder to that gate with the real C# sandbox, so a script that does not compile fails here.
public sealed class FeatureCompileFacts
{
    [Fact]
    public async Task EveryFeatureScriptCompilesInTheRealSandbox()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create()
            .WithModule<AppsModule, AppsOptions>(options => options.Features = SolutionProjects.Features)
            .WithModule<CSharpModule>()
            .StartAsync(ct);
        var sandbox = brain.Services.GetRequiredService<IScriptSandbox>();
        Assert.True(sandbox.CanRun);

        var errors = new List<string>();
        var compiled = 0;
        foreach (var feature in new FeatureDirectory(SolutionProjects.Features).Load())
        {
            // Mirrors what an installed app deploys (AppNeuron.Sources): its program files only. app.cs is the
            // declaration, kept as manifest and spec state, so a synapse that names `App` compiles at boot but
            // not in the brain's runtime.
            var bundle = feature.Bundle;
            var scripts = bundle.Files.Where(file => AppFiles.IsProgram(file.Key))
                .ToDictionary(file => file.Key, file => file.Value, StringComparer.Ordinal);
            if (scripts.Count == 0) { continue; }
            var check = await sandbox.Compile(scripts, ct);
            compiled++;
            var reported = check.Errors.Select(error => $"{feature.Owner}/{bundle.Name}: {error.File}:{error.Line} {error.Id} {error.Message}").ToArray();
            if (!check.Success && reported.Length == 0) { reported = [$"{feature.Owner}/{bundle.Name}: did not compile and reported no error"]; }
            errors.AddRange(reported);
        }

        Assert.True(compiled > 0, "no feature bundle with .cs files was found under " + SolutionProjects.Features);
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    // L0 over the features folder: every shipped synapse is exactly one top-level reaction, the gate boot holds them to.
    [Fact]
    public void TheShippedFeaturesHaveTheShape()
    {
        var errors = new List<string>();
        var synapses = 0;
        foreach (var feature in new FeatureDirectory(SolutionProjects.Features).Load())
        {
            var bundle = feature.Bundle;
            synapses += bundle.Files.Count(file => AppFiles.IsProgram(file.Key) && !SynapseShape.IsShared(file.Value));
            errors.AddRange(SynapseShape.Check(bundle.Files).Select(error => $"{feature.Owner}/{bundle.Name}: {error.File}:{error.Line} {error.Id} {error.Message}"));
        }

        Assert.True(synapses > 0, "no synapse was found under " + SolutionProjects.Features);
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }
}
