using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Apps;

// Runs the app's tests.cs through the sandbox runner and writes the verdicts back to the
// neuron. Verdicts bind to "## Scenario:" headings in the spec: every declared scenario
// needs a verdict, and a verdict naming no declared scenario fails the run.
internal static class AppVerifyJob
{
    public static void Enqueue(IServiceProvider services, string appKey)
        => services.GetRequiredService<AppWorkQueue>().Enqueue($"verify {appKey}", (scope, ct) => Run(scope, appKey, ct));

    public static async Task Run(IServiceProvider services, string appKey, CancellationToken cancellationToken)
    {
        var app = services.GetRequiredService<IGrainFactory>().GetGrain<IAppV2>(appKey);
        var snapshot = await app.Read();
        var tests = snapshot.Files.GetValueOrDefault(AppFiles.TestsPath, "");
        var run = await services.GetRequiredService<ITestScriptRunner>().RunAsync(appKey, tests, cancellationToken);
        var declared = Scenarios(snapshot.Spec);
        var verdicts = new List<AppScenarioVerdict>(run.Scenarios);
        var green = run.Green;
        foreach (var scenario in declared.Where(name => !run.Scenarios.Any(verdict => verdict.Name == name)))
        {
            verdicts.Add(new(scenario, false, "The tests reported no verdict for this scenario."));
            green = false;
        }
        if (declared.Count > 0 && run.Scenarios.Any(verdict => !declared.Contains(verdict.Name)))
        { green = false; }
        await app.RecordVerdicts([.. verdicts], green);
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
