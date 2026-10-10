using DigitalBrain.Apps;
using DigitalBrain.Framework;
using DigitalBrain.Microsoft.CSharp;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.OS.Tests.Unit;

// Boot compiles every shipped feature only when a real sandbox is present; this holds the features
// folder to that gate with the real C# sandbox, so a script that does not compile fails here.
public sealed class FeatureCompileFacts
{
    [Fact(Timeout = 240_000)]
    public async Task EveryFeatureScriptCompilesInTheRealSandbox()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create()
            .WithModule<AppsModule, AppsOptions>(options => options.Features = SolutionProjects.Features)
            .WithModule<CSharpModule>()
            .WithReminders()
            .StartAsync(ct);
        var sandbox = brain.SiloServices.GetRequiredService<IScriptSandbox>();
        Assert.True(sandbox.CanRun);

        var errors = new List<string>();
        var compiled = 0;
        foreach (var feature in new FeatureDirectory(SolutionProjects.Features).Load())
        {
            // Mirrors FeatureCatalog.RequireCompiles: the bundle's .cs files plus the written declaration.
            var bundle = feature.Bundle;
            var scripts = bundle.Files.Where(file => file.Key.EndsWith(".cs", StringComparison.Ordinal))
                .ToDictionary(file => file.Key, file => file.Value, StringComparer.Ordinal);
            if (scripts.Count == 0) { continue; }
            scripts[AppFiles.DeclarationPath] = AppDeclaration.Write(new(bundle.Name, bundle.Version, bundle.Manifest, bundle.Spec));
            var check = await sandbox.Compile(scripts, ct);
            compiled++;
            var reported = check.Errors.Select(error => $"{feature.Owner}/{bundle.Name}: {error.File}:{error.Line} {error.Id} {error.Message}").ToArray();
            if (!check.Success && reported.Length == 0) { reported = [$"{feature.Owner}/{bundle.Name}: did not compile and reported no error"]; }
            errors.AddRange(reported);
        }

        Assert.True(compiled > 0, "no feature bundle with .cs files was found under " + SolutionProjects.Features);
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }
}
