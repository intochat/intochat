namespace DigitalBrain.Testing.Scenarios;

/// <summary>Runs a scenario through a driver and journals it on a run neuron.</summary>
public static class ScenarioRunner
{
    /// <summary>Runs every step in order and stops at the first failing step with its reason. A driver exception becomes a failed
    /// verdict with the driver's evidence: this never throws for a driver failure (only cancellation of <paramref name="ct"/> does).</summary>
    /// <param name="scenario">The scenario.</param>
    /// <param name="driver">The surface to run it on.</param>
    /// <param name="run">The run neuron that journals it.</param>
    /// <param name="ct">Cancellation.</param>
    public static async Task<RunVerdict> Run(UiScenario scenario, IScenarioDriver driver, IRun run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(run);
        await run.Start(scenario.Name, driver.Name).ConfigureAwait(false);
        var verdict = new RunVerdict(true);
        for (var index = 0; index < scenario.Steps.Count; index++)
        {
            var step = scenario.Steps[index];
            string? failure;
            string? shown = null;
            try
            {
                (failure, shown) = await Take(step, driver, ct).ConfigureAwait(false);
            }
            catch (Exception error) when (!ct.IsCancellationRequested)
            {
                System.Diagnostics.Trace.TraceWarning($"Scenario {scenario.Name} step {index}: the {driver.Name} driver threw {error}");
                failure = $"{step.Target}: the {driver.Name} driver failed: {error.GetType().Name}: {error.Message}";
            }
            var evidence = await Evidence(driver, index, ct).ConfigureAwait(false);
            var outcome = failure is null ? (step is Act ? "acted" : "held") : "failed: " + failure;
            await run.Step(index, step, outcome, shown, evidence).ConfigureAwait(false);
            if (failure is null) { continue; }
            verdict = new RunVerdict(false, index, failure, evidence);
            break;
        }
        await run.Finish(verdict).ConfigureAwait(false);
        return verdict;
    }

    // Failure is null when the step held, else why it failed; Shown is what an Expect read.
    private static async Task<(string? Failure, string? Shown)> Take(ScenarioStep step, IScenarioDriver driver, CancellationToken ct)
    {
        switch (step)
        {
            case Act act:
                await driver.Act(act, ct).ConfigureAwait(false);
                return (null, null);
            case Expect expect:
                var shown = await driver.Read(expect.Target, ct).ConfigureAwait(false);
                return (shown.Contains(expect.Contains, StringComparison.Ordinal)
                    ? null
                    : $"{expect.Target}: expected \"{expect.Contains}\" but it shows \"{Clip(shown)}\"", Clip(shown));
            default:
                return ($"{step.Target}: unknown step {step.GetType().Name}", null);
        }
    }

    // Evidence is asked for at every step and is best effort: a driver that cannot capture any leaves the verdict without evidence, never without a verdict.
    private static async Task<string?> Evidence(IScenarioDriver driver, int index, CancellationToken ct)
    {
        try { return await driver.Evidence(index, ct).ConfigureAwait(false); }
        catch (Exception error) when (!ct.IsCancellationRequested)
        {
            System.Diagnostics.Trace.TraceWarning($"The {driver.Name} driver captured no evidence at step {index}: {error}");
            return null;
        }
    }

    private static string Clip(string text) => text.Length <= 200 ? text : text[..200] + "...";
}
