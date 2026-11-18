using DigitalBrain.Neurons;

namespace DigitalBrain.Testing.Scenarios;

/// <summary>A run began.</summary>
/// <param name="Scenario">The scenario name.</param>
/// <param name="Driver">The driver name.</param>
[Signal("test.run.started")]
public sealed record RunStarted(string Scenario, string Driver) : Signal;

/// <summary>A step was taken.</summary>
/// <param name="Index">The step index.</param>
/// <param name="Outcome">What happened: acted, held, or failed with the reason.</param>
/// <param name="EvidenceRef">The driver's evidence, or none.</param>
[Signal("test.run.step")]
public sealed record StepTaken(int Index, string Outcome, string? EvidenceRef = null) : Signal;

/// <summary>A run ended.</summary>
/// <param name="Verdict">How it ended.</param>
[Signal("test.run.finished")]
public sealed record RunFinished(RunVerdict Verdict) : Signal;

/// <summary>What a run neuron holds.</summary>
/// <param name="Scenario">The scenario name.</param>
/// <param name="Driver">The driver name.</param>
/// <param name="Steps">How many steps were taken.</param>
/// <param name="Verdict">How it ended, or none while running.</param>
public sealed record ScenarioRunState(string Scenario, string Driver, int Steps, RunVerdict? Verdict)
{
    /// <summary>An empty run.</summary>
    public ScenarioRunState() : this("", "", 0, null) { }
}

/// <summary>One scenario run: a neuron, so a run is observable like any neuron.</summary>
public interface IRun
{
    /// <summary>Starts the run.</summary>
    /// <param name="scenario">The scenario name.</param>
    /// <param name="driver">The driver name.</param>
    Task Start(string scenario, string driver);

    /// <summary>Records a step.</summary>
    /// <param name="index">The step index.</param>
    /// <param name="outcome">What happened.</param>
    /// <param name="evidenceRef">The driver's evidence, or none.</param>
    Task Step(int index, string outcome, string? evidenceRef);

    /// <summary>Ends the run.</summary>
    /// <param name="verdict">How it ended.</param>
    Task Finish(RunVerdict verdict);

    /// <summary>The run so far.</summary>
    Task<ScenarioRunState> Read();
}

/// <summary>Journals a scenario run: <see cref="RunStarted"/>, one <see cref="StepTaken"/> per step, then <see cref="RunFinished"/>.</summary>
[Neuron("test.run")]
public sealed class RunNeuron : Neuron<ScenarioRunState>, IRun
{
    /// <inheritdoc />
    public Task Start(string scenario, string driver)
        => Save(new ScenarioRunState(scenario, driver, 0, null), [new RunStarted(scenario, driver)]);

    /// <inheritdoc />
    public Task Step(int index, string outcome, string? evidenceRef)
        => Save(State with { Steps = State.Steps + 1 }, [new StepTaken(index, outcome, evidenceRef)]);

    /// <inheritdoc />
    public Task Finish(RunVerdict verdict)
        => Save(State with { Verdict = verdict }, [new RunFinished(verdict)]);

    /// <inheritdoc />
    public Task<ScenarioRunState> Read() => Task.FromResult(State);
}
