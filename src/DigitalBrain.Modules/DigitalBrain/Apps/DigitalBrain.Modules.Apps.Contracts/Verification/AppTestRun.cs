namespace DigitalBrain.Apps;

[GenerateSerializer, Alias("apps.scenario-verdict")]
public sealed record AppScenarioVerdict(
    [property: Id(0)] string Name,
    [property: Id(1)] bool Passed,
    [property: Id(2)] string Message,
    [property: Id(3)] string? ScenarioId = null);

// The tests script's whole verdict: its dbtest lines and its exit code. A run that reported no
// scenario is never green, so a script that crashes early cannot pass by silence.
[GenerateSerializer, Alias("apps.test-run")]
public sealed record AppTestRun(
    [property: Id(0)] AppScenarioVerdict[] Scenarios,
    [property: Id(1)] int? ExitCode)
{
    public bool Green => ExitCode == 0 && Scenarios.Length > 0 && Scenarios.All(scenario => scenario.Passed);
}
