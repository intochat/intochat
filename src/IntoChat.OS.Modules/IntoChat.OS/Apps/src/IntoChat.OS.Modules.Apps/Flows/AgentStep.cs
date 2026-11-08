using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Flows;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Agents.Signals;

namespace IntoChat.OS.Modules.Apps.Flows;

// The state an agent step keeps: the run it waits on, the reply that ended it, or the error that failed it.
public abstract class AgentInner
{
    public string? AgentStep { get; set; }

    public string? RunId { get; set; }

    public string? Reply { get; set; }

    public string? AgentError { get; set; }
}

// The neuron type an agent step's agent has: the AI module's "agent" unless a host (a test) registers another.
public sealed class AgentStepOptions
{
    public const string DefaultAgentType = "agent";

    public string AgentType { get; init; } = DefaultAgentType;
}

// A flow whose steps may ask the model. Ask starts a run on the flow's own agent ({brain}/{AgentStepOptions.AgentType}/flow-agent/{flowKey}) and returns an
// External wait; the run's TurnCompleted (by run id) stores the reply and resumes the step, TurnFailed records the error and resumes it,
// and the next step fails the flow with that error. The reply is written to the inner state the resume saves.
public abstract class AgentFlow<T> : Flow<T> where T : AgentInner, new()
{
    private static readonly AgentDefinition FlowAgent = new()
    {
        DisplayName = "Flow agent",
        Description = "Answers one step of a flow.",
        Instructions = "You answer one step of a durable flow. Reply with exactly what the step asks for, as JSON when it asks for JSON.",
    };

    protected abstract Task<StepResult<T>> RunStep(string step, T inner, CancellationToken ct);

    protected sealed override Task<StepResult<T>> Run(string step, T inner, CancellationToken ct)
    {
        if (inner.AgentError is { } error) { throw new FlowFailed(inner.AgentStep ?? step, error); }
        return RunStep(step, inner, ct);
    }

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        await Context.Synapses.EnsureBrainWide(typeof(TurnCompleted), typeof(TurnFailed));
        await base.OnActivate(cancellationToken);
    }

    // The run id is the flow's key and the step, so a step run again asks the same run.
    protected async Task<StepResult<T>> Ask(string step, T inner, string prompt)
    {
        var type = (Context.Services.GetService(typeof(AgentStepOptions)) as AgentStepOptions)?.AgentType ?? AgentStepOptions.DefaultAgentType;
        var agent = Context.Get<IAgent>(new NeuronId(Id.Brain, type, "flow-agent/" + Id.Key));
        if ((await agent.Read()).Revision == 0)
        {
            try { await agent.Configure(FlowAgent, 0); }
            catch (InvalidOperationException error) when (error.Message.Contains("revision conflict", StringComparison.OrdinalIgnoreCase)) { _ = error; }
        }
        inner.AgentStep = step;
        inner.Reply = null;
        inner.AgentError = null;
        inner.RunId = await agent.Start(AgentTurn.Text(prompt, runId: $"{Id.Key}/{step}"), Id.ToString());
        return new StepResult<T>(inner, new Waiting(step, WaitReason.External, null, null));
    }

    [On]
    public Task OnTurnCompleted(TurnCompleted done)
    {
        ArgumentNullException.ThrowIfNull(done);
        if (WaitingStep(done.RunId) is not { } step) { return Task.CompletedTask; }
        State.Inner.Reply = done.Reply;
        return Resume(step, WaitOutcome.Arrived, done.AgentId);
    }

    [On]
    public Task OnTurnFailed(TurnFailed failed)
    {
        ArgumentNullException.ThrowIfNull(failed);
        if (WaitingStep(failed.RunId) is not { } step) { return Task.CompletedTask; }
        State.Inner.AgentError = failed.Cancelled ? "the agent run was cancelled" : failed.Error;
        return Resume(step, WaitOutcome.Arrived, failed.AgentId);
    }

    private string? WaitingStep(string runId)
        => State is { Status: FlowStatus.Waiting, Waiting: { Reason: WaitReason.External } waiting } && State.Inner.RunId == runId && waiting.Step == State.Inner.AgentStep
            ? waiting.Step : null;

    // The JSON object in a model reply: from the first '{' to the last '}', so a fenced or prefaced reply still parses.
    internal static TReply? ParseReply<TReply>(string step, string? reply, JsonSerializerOptions options)
    {
        var text = reply ?? throw new FlowFailed(step, "the agent did not reply");
        var start = text.IndexOf('{', StringComparison.Ordinal);
        var end = text.LastIndexOf('}');
        if (start < 0 || end < start) { throw new FlowFailed(step, $"the agent's reply is not JSON: {text}"); }
        try { return JsonSerializer.Deserialize<TReply>(text[start..(end + 1)], options); }
        catch (JsonException error) { throw new FlowFailed(step, $"the agent's reply is not valid JSON: {error.Message}"); }
    }
}
