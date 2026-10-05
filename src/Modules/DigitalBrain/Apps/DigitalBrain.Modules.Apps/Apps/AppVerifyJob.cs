using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Apps;

// Runs an app's tests.cs against a scratch copy — never the live app, whose settings and
// state belong to the person — and writes the verdicts back. Verdicts bind to "## Scenario:"
// headings in the spec: every declared scenario needs a verdict, and a verdict naming no
// declared scenario fails the run.
internal static class AppVerifyJob
{
    public static void Enqueue(IServiceProvider services, string appKey)
        => services.GetRequiredService<AppWorkQueue>().Enqueue($"verify {appKey}", (scope, ct) => Run(scope, appKey, ct));

    public static async Task Run(IServiceProvider services, string appKey, CancellationToken cancellationToken)
    {
        var grains = services.GetRequiredService<IGrainFactory>();
        var app = grains.GetGrain<IApp>(appKey);
        var snapshot = await app.Read();
        var bundle = new AppBundle(snapshot.Name, snapshot.Version, snapshot.Manifest, snapshot.Spec,
            new(snapshot.Files, StringComparer.Ordinal));
        var (verdicts, green) = await RunBundle(grains, services.GetRequiredService<ITestScriptRunner>(),
            bundle, appKey + "--verify-" + Guid.NewGuid().ToString("N"), cancellationToken);
        await app.RecordVerdicts(verdicts, green);
    }

    // Imports the bundle at the scratch key, runs its tests there, and deletes the copy.
    public static async Task<(AppScenarioVerdict[] Verdicts, bool Green)> RunBundle(
        IGrainFactory grains, ITestScriptRunner runner, AppBundle bundle, string scratchKey, CancellationToken cancellationToken)
    {
        var scratch = grains.GetGrain<IApp>(scratchKey);
        try
        {
            await scratch.Import(bundle);
            var run = await runner.RunAsync(scratchKey, bundle.Files.GetValueOrDefault(AppFiles.TestsPath, ""), cancellationToken);
            var declared = Scenarios(bundle.Spec);
            var verdicts = new List<AppScenarioVerdict>(run.Scenarios);
            var green = run.Green;
            foreach (var scenario in declared.Where(name => !run.Scenarios.Any(verdict => verdict.Name == name)))
            {
                verdicts.Add(new(scenario, false, "The tests reported no verdict for this scenario."));
                green = false;
            }
            if (declared.Count > 0 && run.Scenarios.Any(verdict => !declared.Contains(verdict.Name)))
            { green = false; }
            return ([.. verdicts], green);
        }
        finally
        {
            try { await scratch.Delete(); }
            catch (Exception error) when (error is not OperationCanceledException) { }
        }
    }

    internal static HashSet<string> Scenarios(string spec)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in (spec ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("## Scenario:", StringComparison.Ordinal))
            { names.Add(line["## Scenario:".Length..].Trim()); }
        }
        return names;
    }
}
