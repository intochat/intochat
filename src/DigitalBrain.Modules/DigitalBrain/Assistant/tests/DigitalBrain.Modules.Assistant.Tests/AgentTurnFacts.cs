using System.Runtime.CompilerServices;
using System.Text;
using DigitalBrain;
using DigitalBrain.AI;
using DigitalBrain.AI.Agents;
using DigitalBrain.AI.Metering;
using DigitalBrain.AI.Models;
using DigitalBrain.Assistant;
using DigitalBrain.Compute;
using DigitalBrain.Framework;
using Xunit;
using DigitalBrain.Framework.Metering;

namespace DigitalBrain.Modules.Assistant.Tests;

public sealed class AgentTurnFacts
{
    [Fact]
    public async Task RunModelForwardsTheSelectedProfileInsteadOfTheServerDefault()
    {
        var runner = new RecordingRunner();
        var selected = new ModelSelection("work");
        var configuration = new AssistantOptions { Model = "IGpt56Luna" };

        await AssistantTurnExecution.RunModel("run", "hello", AssistantDefinition.Product, "reply",
            configuration, runner.Run, _ => Task.CompletedTask,
            new StringBuilder(), new IntentActivity(), TestContext.Current.CancellationToken, selected);

        Assert.Equal(selected, runner.Request!.Model);
    }

    // The HTTP adapter must forward the application neuron's instructions unchanged.
    [Fact]
    public async Task RunModelPreservesTheApplicationDefinitionWithoutRecomposingIt()
    {
        var runner = new RecordingRunner();
        var definition = new AgentDefinition { Instructions = "Application-owned instructions and summary", Tools = ["custom_tool"] };
        var text = new StringBuilder();

        await AssistantTurnExecution.RunModel("run", "hello", definition, "run-reply",
            new AssistantOptions(), runner.Run, _ => Task.CompletedTask, text, new IntentActivity(), TestContext.Current.CancellationToken);

        Assert.NotNull(runner.Request);
        Assert.Equal(definition.Instructions, runner.Request!.Instructions);
        Assert.Equal(definition.Tools, runner.Request.Tools);
    }

    // A refused or malformed call is answered to the model for repair; the run itself goes on.
    [Fact]
    public async Task AToolErrorIsTheModelsToRepairAndDoesNotFailTheRun()
    {
        var runner = new ToolEventRunner(
            new AgentTurnEvent.ToolCompleted("call", "neurons_call", """{"isError":true,"message":"IApp has no method Read taking 3 arguments."}"""));
        var emitted = new List<object>();

        await AssistantTurnExecution.RunModel("run", "read it", AssistantDefinition.Product, "run-reply", new AssistantOptions(), runner.Run,
            value => { emitted.Add(value); return Task.CompletedTask; }, new StringBuilder(), new IntentActivity(), TestContext.Current.CancellationToken);

        Assert.Contains(emitted, value => value.ToString()!.Contains("TOOL_CALL_RESULT", StringComparison.Ordinal));
    }

    // The shadow Compute on a receipt is the intent's durable usage priced by the one price book.
    [Fact]
    public void ShadowPriceSumsEveryTokenClassForTheIntent()
    {
        using var intent = IntentContext.Begin("intent-shadow", "scope");
        intent.AddUsage(new IntentUsageEntry<TokenUsageEntry>(new(MeterKind.Chat, "OpenAI", "gpt-5.6-luna",
            1_000_000, 0, null, 1_000_000, 2_000_000, true, DateTimeOffset.UtcNow)));

        var (modelCalls, compute) = AgentReceipts.ShadowPrice(intent.UsageOf<TokenUsageEntry>(), new PriceBook());

        Assert.Equal(1, modelCalls);
        Assert.Equal(250m + 1000m, compute);
    }

    private sealed class ToolEventRunner(params AgentTurnEvent[] events)
    {
        public async IAsyncEnumerable<AgentTurnEvent> Run(AgentTurn _)
        {
            await Task.Yield();
            foreach (var item in events) { yield return item; }
            yield return new AgentTurnEvent.Finished();
        }
    }

    private sealed class RecordingRunner
    {
        public AgentTurn? Request { get; private set; }
        public async IAsyncEnumerable<AgentTurnEvent> Run(AgentTurn turn)
        {
            Request = turn;
            await Task.Yield();
            yield return new AgentTurnEvent.Finished();
        }
    }
}
