using DigitalBrain.Testing.Scenarios;
using OS = DigitalBrain.Testing.TestOS;

namespace DigitalBrain.Testing.Tests.Unit.Scenarios;

public sealed class ScenarioRunnerFacts
{
    private static readonly NeuronId RunId = NeuronId.Parse("scenario-runs/test.run/r1");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly UiScenario Sample = new("sample", "demo",
    [
        new Act("nav", "control.select", null),
        new Expect(".", "Hello"),
        new Expect("nav", "Never shown"),
        new Act(".", "demo.never", null),
    ]);

    [Fact]
    public async Task Passing_scenario_journals_start_steps_finish()
    {
        using var platform = TestStamp.Kernel();
        await using var os = OS.Local().Register<RunNeuron>();
        var driver = new FakeDriver { Shown = "Hello there" };
        var scenario = Sample with { Steps = Sample.Steps.Take(2).ToArray() };

        var verdict = await ScenarioRunner.Run(scenario, driver, os.Get<IRun>(RunId), Ct);
        await os.Idle();

        Assert.Equal(new RunVerdict(true), verdict);
        Assert.Equal(["nav:control.select"], driver.Acts);
        var signals = os.Journal(RunId.Brain).Signals(RunId).Select(entry => entry.Signal).ToArray();
        Assert.Equal<Signal>(
            [new RunStarted("sample", "fake"), new StepTaken(0, "acted"), new StepTaken(1, "held"), new RunFinished(verdict)], signals);
        Assert.Equal(new ScenarioRunState("sample", "fake", 2, verdict), await os.Get<IRun>(RunId).Read());
    }

    [Fact]
    public async Task First_failing_Expect_stops_with_reason()
    {
        using var platform = TestStamp.Kernel();
        await using var os = OS.Local().Register<RunNeuron>();
        var driver = new FakeDriver { Shown = "Hello there" };

        var verdict = await ScenarioRunner.Run(Sample, driver, os.Get<IRun>(RunId), Ct);
        await os.Idle();

        Assert.False(verdict.Passed);
        Assert.Equal(2, verdict.FailedStep);
        Assert.Contains("Never shown", verdict.Reason, StringComparison.Ordinal);
        Assert.Contains("Hello there", verdict.Reason, StringComparison.Ordinal);
        Assert.Equal("evidence-2", verdict.EvidenceRef);
        Assert.Equal(["nav:control.select"], driver.Acts);
        var last = os.Journal(RunId.Brain).Signals(RunId).Select(entry => entry.Signal).TakeLast(2).ToArray();
        Assert.Equal("evidence-2", Assert.IsType<StepTaken>(last[0]).EvidenceRef);
        Assert.Equal(verdict, Assert.IsType<RunFinished>(last[1]).Verdict);
    }

    [Fact]
    public async Task Driver_exception_becomes_failed_verdict()
    {
        using var platform = TestStamp.Kernel();
        await using var os = OS.Local().Register<RunNeuron>();
        var driver = new FakeDriver { Shown = "Hello", Throws = true };

        var verdict = await ScenarioRunner.Run(Sample, driver, os.Get<IRun>(RunId), Ct);
        await os.Idle();

        Assert.False(verdict.Passed);
        Assert.Equal(0, verdict.FailedStep);
        Assert.Contains("the screen is gone", verdict.Reason, StringComparison.Ordinal);
        Assert.Equal("evidence-0", verdict.EvidenceRef);
        Assert.IsType<RunFinished>(os.Journal(RunId.Brain).Signals(RunId)[^1].Signal);
    }

    [Fact]
    public void Scenarios_round_trip_through_json()
    {
        var args = System.Text.Json.JsonSerializer.SerializeToElement(new { id = "x" });
        UiScenario[] scenarios = [Sample with { Steps = [new Act("nav", "control.select", args), new Expect(".", "x")] }];

        var read = ScenarioJson.Read(ScenarioJson.Write(scenarios));

        Assert.Equal(ScenarioJson.Write(scenarios), ScenarioJson.Write(read));
        Assert.IsType<Act>(read[0].Steps[0]);
    }

    private sealed class FakeDriver : IScenarioDriver
    {
        public string Shown { get; init; } = "";

        public bool Throws { get; init; }

        public List<string> Acts { get; } = [];

        public string Name => "fake";

        public Task Act(Act step, CancellationToken ct)
        {
            if (Throws) { throw new InvalidOperationException("the screen is gone"); }
            Acts.Add($"{step.Target}:{step.Signal}");
            return Task.CompletedTask;
        }

        public Task<string> Read(string target, CancellationToken ct) => Task.FromResult(Shown);

        public Task<string?> Evidence(int stepIndex, CancellationToken ct) => Task.FromResult<string?>($"evidence-{stepIndex}");
    }
}
