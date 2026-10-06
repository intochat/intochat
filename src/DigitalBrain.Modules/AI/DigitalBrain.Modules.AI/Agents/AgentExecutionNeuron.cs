using System.Runtime.CompilerServices;
using DigitalBrain;
using DigitalBrain.AI.Metering;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Enforcement;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime;
using DigitalBrain.Framework.Metering;

namespace DigitalBrain.AI.Agents;

[GrainType("ai.agent-execution")]
internal sealed class AgentExecutionNeuron(IAgentTurnRunner runner, IIntentUsageSink usage,
    ModelProfiles profiles, IOptionsMonitor<AIOptions> options, ILogger<AgentExecutionNeuron> logger) : Neuron, IAgentExecution, IAgentTurnDriver
{
    public override NeuronAccess Access(string operation) => new(BrainScope.CurrentId());

    public Task<ModelCatalog> Models(string? defaultModel = null)
        => new AgentModelCatalog(profiles, options, defaultModel).Read();
    public Task<AgentModelSelection?> SelectModel(string? id, string? defaultModel = null)
        => new AgentModelCatalog(profiles, options, defaultModel).Select(id);

    public async IAsyncEnumerable<AgentTurnEvent> Run(AgentTurnRequest request, string intentId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (CallerContextStamper.TryGet(out var caller) && caller.Kind != CallerKind.Platform
            && request.ScopeId != BrainScope.CurrentId())
        { throw new UnauthorizedAccessException("Agent execution belongs to another brain."); }
        using var intent = IntentContext.Create(intentId, request.ScopeId);
        try
        {
            var events = runner.RunAsync(request, ct).GetAsyncEnumerator(ct);
            try
            {
                while (true)
                {
                    bool next;
                    using (intent.Enter()) { next = await events.MoveNextAsync(); }
                    if (!next) { break; }
                    yield return events.Current;
                }
            }
            finally
            {
                using (intent.Enter()) { await events.DisposeAsync(); }
            }
        }
        finally
        {
            try { await usage.FlushAsync(intent.Snapshot(), CancellationToken.None); }
            catch (Exception error)
            { logger.LogWarning(error, "Agent usage flush failed for intent {IntentId}", intentId); }
        }
    }

    public async Task<string> Start(AgentTurnStart turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        var scope = BrainScope.CurrentId();
        // The conversation, like every brain neuron key, starts with its brain scope.
        if (!turn.Conversation.StartsWith(scope + "/", StringComparison.Ordinal))
        { throw new UnauthorizedAccessException("The conversation belongs to another brain."); }
        if (turn.Message.Length is 0 or > 32000) { throw new ArgumentException("A message has 1-32000 characters.", nameof(turn)); }
        var runId = Guid.NewGuid().ToString("N");
        await GrainFactory.GetGrain<IAgent>(turn.Conversation).BeginConversation(new(runId, turn.Message));
        await this.AsReference<IAgentTurnDriver>().Drive(runId, turn);
        return runId;
    }

    public async Task Drive(string runId, AgentTurnStart turn)
    {
        var conversation = GrainFactory.GetGrain<IAgent>(turn.Conversation);
        var sequence = 0;
        var text = new System.Text.StringBuilder();
        Task Progress(string kind, string? content, string? tool = null, string? payload = null)
            => PublishAsync(new Signals.AgentTurnProgressed(runId, kind, content, tool, payload, ++sequence));
        try
        {
            var history = (await conversation.ReadConversation()).Turns.Where(item => item.RunId != runId).ToArray();
            var model = await ModelFor(turn.Model);
            var request = new AgentTurnRequest("assistant", runId, BrainScope.CurrentId(), history, turn.Message, model,
                Instructions: turn.SystemPrompt.Length == 0 ? null : turn.SystemPrompt, ToolNames: turn.Tools);
            await foreach (var item in Run(request, runId))
            {
                switch (item)
                {
                    case AgentTurnEvent.Text delta:
                        text.Append(delta.Content);
                        await Progress("text", delta.Content);
                        break;
                    case AgentTurnEvent.ToolStarted started:
                        await Progress("tool-call", null, started.Name, started.Arguments);
                        break;
                    case AgentTurnEvent.ToolCompleted completed:
                        await Progress("tool-result", null, completed.Name, completed.Result);
                        if (Card(completed.Result) is { } card) { await Progress("card", null, completed.Name, card); }
                        break;
                    case AgentTurnEvent.ToolFailed failed:
                        await Progress("tool-result", failed.Message, failed.Name,
                            System.Text.Json.JsonSerializer.Serialize(new { isError = true, code = failed.Code, message = failed.Message }));
                        break;
                    case AgentTurnEvent.Completed done when done.Usage is not null:
                        await Progress("receipt", null, null, System.Text.Json.JsonSerializer.Serialize(done.Usage));
                        break;
                    case AgentTurnEvent.Failed failure:
                        throw new InvalidOperationException(failure.Message);
                }
            }
            await conversation.CompleteConversation(new(runId, turn.Message, text.ToString(), []));
            await PublishAsync(new Signals.AgentTurnCompleted(runId));
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "Agent turn {RunId} failed.", runId);
            try { await conversation.CompleteConversation(new(runId, turn.Message, "The turn failed: " + error.Message, [])); }
            catch (Exception) { /* The run is no longer active; nothing to keep. */ }
            await PublishAsync(new Signals.AgentTurnFailed(runId, error.Message));
        }
    }

    private async Task<AgentModelSelection?> ModelFor(string model)
        => model.Length == 0 ? null
        : model.StartsWith(DigitalBrain.AI.Scripted.IScriptedLLM.ModelPrefix, StringComparison.Ordinal) ? new(Model: model)
        : await SelectModel(model) ?? throw new ArgumentException($"Model '{model}' is not in the catalog.");

    // A tool result may carry a UI card on its "_ui" channel; the card is its own progress item.
    private static string? Card(string result)
    {
        try
        {
            using var payload = System.Text.Json.JsonDocument.Parse(result);
            return payload.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && payload.RootElement.TryGetProperty("_ui", out var card) && card.ValueKind == System.Text.Json.JsonValueKind.Object
                ? card.GetRawText() : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
