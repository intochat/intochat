using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Flows;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.Flutter.Conversations;

namespace IntoChat.OS.Modules.Apps.Flows;

// The state an agent step keeps: the run it waits on, the reply that ended it, or the error that failed it.
public abstract class AgentInner
{
    public string? AgentStep { get; set; }

    public string? RunId { get; set; }

    public string? Reply { get; set; }

    public string? AgentError { get; set; }
}

// The neuron type an agent step's conversation has: the AI module's "conversation" unless a host (a test) registers another.
public sealed class AgentStepOptions
{
    public const string DefaultConversationType = IConversation.Type;

    public string ConversationType { get; init; } = DefaultConversationType;
}

// A flow whose steps may ask the model. Ask starts a run on the flow's own one-seat conversation
// ({brain}/{AgentStepOptions.ConversationType}/flow-conv/{flowKey}) and returns an External wait; ConversationChanged
// reads the completed or failed run (by run id), stores the reply or error, and resumes the step, and the next step
// fails the flow with that error. The reply is written to the inner state the resume saves.
public abstract class AgentFlow<T>(AgentStepOptions? options = null) : Flow<T> where T : AgentInner, new()
{
    private const string SeatId = "flow";

    private static readonly AgentDefinition FlowAgent = new()
    {
        DisplayName = "Flow agent",
        Description = "Answers one step of a flow.",
        Instructions = "You answer one step of a durable flow. Reply with exactly what the step asks for, as JSON when it asks for JSON.",
    };

    private NeuronId ConversationNeuronId => new(Id.Brain, options?.ConversationType ?? AgentStepOptions.DefaultConversationType, "flow-conv/" + Id.Key);

    private IConversation Conversation => Context.Get<IConversation>(ConversationNeuronId);

    /// <summary>
    /// Builds the <see cref="AgentDefinition"/> seated for <paramref name="step"/>.
    /// Override to equip a flow worker with a least-privilege <see cref="AgentDefinition.Contracts"/> subset.
    /// </summary>
    protected virtual AgentDefinition BuildAgentDefinition(string step, T inner) => FlowAgent;

    protected abstract Task<StepResult<T>> RunStep(string step, T inner, CancellationToken ct);

    protected sealed override Task<StepResult<T>> Run(string step, T inner, CancellationToken ct)
    {
        if (inner.AgentError is { } error) { throw new FlowFailed(inner.AgentStep ?? step, error); }
        return RunStep(step, inner, ct);
    }

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        await Context.Wires.Ensure<ConversationChanged>(ConversationNeuronId);
        await base.OnActivate(cancellationToken);
    }

    protected async Task<StepResult<T>> Ask(string step, T inner, string prompt)
    {
        var conversation = Conversation;
        await conversation.Seat(SeatId, BuildAgentDefinition(step, inner));
        var before = await conversation.Read();

        inner.AgentStep = step;
        inner.Reply = null;
        inner.AgentError = null;

        if (before.Active is { } running && string.Equals(running.UserText, prompt, StringComparison.Ordinal))
        {
            inner.RunId = running.RunId;
            return inner.WaitExternal(step);
        }

        await conversation.Say(prompt);
        var after = await conversation.Read();
        if (after.Active is { } active)
        {
            inner.RunId = active.RunId;
            return inner.WaitExternal(step);
        }

        var priorIds = before.Runs.Select(static r => r.RunId).ToHashSet(StringComparer.Ordinal);
        var finished = after.Runs.LastOrDefault(r => !priorIds.Contains(r.RunId)) ?? after.Runs.LastOrDefault();
        if (finished is not null)
        {
            inner.RunId = finished.RunId;
            if (finished.Status == AgentRunStatus.Completed)
            {
                inner.Reply = finished.Reply ?? "";
                return inner.Advance();
            }

            inner.AgentError = FailureMessageOf(finished);
            throw new FlowFailed(step, inner.AgentError);
        }

        inner.RunId = null;
        return inner.WaitExternal(step);
    }

    [On]
    public async Task OnConversationChanged(ConversationChanged changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        if (WaitingStep() is not { } step) { return; }
        var view = await Conversation.Read();
        var run = State.Inner.RunId is { } runId
            ? view.Runs.LastOrDefault(item => string.Equals(item.RunId, runId, StringComparison.Ordinal))
            : (view.Active is null ? view.Runs.LastOrDefault() : null);
        if (run is null) { return; }

        State.Inner.RunId = run.RunId;
        if (run.Status == AgentRunStatus.Completed)
        {
            State.Inner.Reply = run.Reply ?? "";
        }
        else
        {
            State.Inner.AgentError = FailureMessageOf(run);
        }

        await Resume(step, WaitOutcome.Arrived, ConversationNeuronId.ToString());
    }

    private string? WaitingStep()
        => State is { Status: FlowStatus.Waiting, Waiting: { Reason: WaitReason.External } waiting } && waiting.Step == State.Inner.AgentStep
            ? waiting.Step : null;

    private static string FailureMessageOf(AgentRun run)
        => run.Status == AgentRunStatus.Cancelled
            ? "the agent run was cancelled"
            : (run.Error ?? "the agent run failed");

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
