using System.Text.Json;
using DigitalBrain.AI.Agents;
using DigitalBrain.Apps;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Contracts;
using DigitalBrain.Framework.Surface;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.AI.Tests.Integration.Agents;

public sealed class ContractActionFacts
{
    // The brain composes no Apps module, so the turn's contract is put in the host's compiled layer directly.
    private static Task<ITestBrain> AppBrain(IChatClient client, CancellationToken ct)
        => AgentFacts.Builder(client)
            .ConfigureServices(services => services.AddSingleton(new DigitalBrain.Framework.CompiledTypes([typeof(INeuron).Assembly, typeof(IApp).Assembly])))
            .StartAsync(ct);

    [Trait("host", "own")]
    [Fact]
    public async Task ATurnOffersTheNeuronActionsOverOnlyItsContracts()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = Scripted(NeuronSurfaceTools.ListName, []);
        await using var brain = await AppBrain(client, ct);
        using var caller = brain.Owner.AsCaller();
        var agent = brain.Get<IAgent>(AgentFacts.Key("main"));
        var events = await AgentFacts.Collect(agent, AgentTurn.Text("what can you reach?", contracts: [typeof(IApp).FullName!]), ct);
        Assert.Equal([NeuronSurfaceTools.ListName, NeuronSurfaceTools.SearchName, NeuronSurfaceTools.GetName,
            NeuronSurfaceTools.CallName, NeuronSurfaceTools.ContextName], client.OfferedTools);
        // The agent itself now exists in the brain, but the turn composes only IApp and no app exists.
        Assert.Equal("[]", Assert.Single(events.OfType<AgentTurnEvent.ToolCompleted>()).Result);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AContractTheTurnDidNotNameIsARepairableError()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = Scripted(NeuronSurfaceTools.CallName, new()
        {
            ["contract"] = "DigitalBrain.Postgres.IPostgresServer",
            ["key"] = "b/connection/postgres/x",
            ["method"] = "ReadConnection",
            ["arguments"] = "[]"
        });
        await using var brain = await AppBrain(client, ct);
        using var caller = brain.Owner.AsCaller();
        var events = await AgentFacts.Collect(brain.Get<IAgent>(AgentFacts.Key("main")), AgentTurn.Text("read it", contracts: [typeof(IApp).FullName!]), ct);
        Assert.Contains("not a contract this app composes", Assert.Single(events.OfType<AgentTurnEvent.ToolCompleted>()).Result);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ATurnWithoutContractsIsPlainChat()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = Scripted(null, []);
        await using var brain = await AppBrain(client, ct);
        var events = await AgentFacts.Collect(brain.Get<IAgent>(AgentFacts.Key("main")), AgentTurn.Text("hi"), ct);
        Assert.Empty(client.OfferedTools);
        Assert.Single(events.OfType<AgentTurnEvent.Finished>());
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnUnknownContractFailsTheTurnBeforeTheModelIsAsked()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = Scripted(null, []);
        await using var brain = await AppBrain(client, ct);
        var events = await AgentFacts.Collect(brain.Get<IAgent>(AgentFacts.Key("main")), AgentTurn.Text("hi", contracts: ["DigitalBrain.Nope.INope"]), ct);
        Assert.Contains("DigitalBrain.Nope.INope", Assert.Single(events.OfType<AgentTurnEvent.Failed>()).Message);
        Assert.Equal(0, client.Calls);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task TheModelOriginatedMarkEndsWithTheAction()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = Scripted(NeuronSurfaceTools.ContextName, []);
        await using var brain = await AppBrain(client, ct);
        using var caller = brain.Owner.AsCaller();
        var events = await AgentFacts.Collect(brain.Get<IAgent>(AgentFacts.Key("main")), AgentTurn.Text("who am I", contracts: [typeof(IApp).FullName!]), ct);
        Assert.Contains("conversationId", Assert.Single(events.OfType<AgentTurnEvent.ToolCompleted>()).Result, StringComparison.OrdinalIgnoreCase);
        Assert.False(CallerContextStamper.TryGet(out var after) && CallerContextStamper.IsModelOriginated(after));
    }

    [Fact]
    public void ThereIsNoToolVocabulary()
    {
        var contracts = typeof(AgentTurn).Assembly;
        Assert.Null(contracts.GetType("DigitalBrain.AI.Tools.IAgentToolFactory"));
        Assert.Null(contracts.GetType("DigitalBrain.AI.Tools.IAgentToolSource"));
        Assert.Null(contracts.GetType("DigitalBrain.AI.Tools.NativeTools"));
        Assert.Null(typeof(AgentTurn).GetProperty("Tools"));
    }

    private static RecordingClient Scripted(string? tool, Dictionary<string, object?> arguments) => new(tool, arguments);

    // Calls the scripted action once, then answers with the action's result as text.
    private sealed class RecordingClient(string? tool, Dictionary<string, object?> arguments) : IChatClient
    {
        public List<string> OfferedTools { get; } = [];
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (Calls++ == 0) { OfferedTools.AddRange(options?.Tools?.Select(item => item.Name) ?? []); }
            var result = messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().LastOrDefault();
            if (tool is not null && result is null)
            {
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", tool, arguments)])));
            }
            var text = result is null ? "hello" : JsonSerializer.Serialize(result.Result);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in (await GetResponseAsync(messages, options, cancellationToken)).ToChatResponseUpdates()) { yield return update; }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
