using System.Runtime.CompilerServices;
using System.Text.Json;
using IntoChat.OS.AI.Agents;
using IntoChat.OS.AI.Agents.Signals;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Platform.Secrets;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Storage;

namespace IntoChat.OS.Modules.AI.Tests.Integration.Agents;

// A vault value must reach neither a journaled signal nor the stored (and so exported) agent state,
// whichever part of a turn carries it.
public sealed class RedactionFacts
{
    private const string Canary = "canary-7f3a9c2e5b1d";

    [Trait("host", "own")]
    // Quarantined (timing): https://github.com/intochat/os/issues/322
    [Fact(Explicit = true)]
    public async Task AVaultValueNeverReachesSignalsOrStoredAgentState()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Brain(ct);
        using var caller = brain.Owner.AsCaller();
        var agent = brain.Get<IAgent>(AgentFacts.Key("main"));
        await using var signals = await brain.Observe<Signal>(agent, ct);

        var first = await agent.Start(AgentTurn.Text("echo the key", runId: "turn-1", contracts: [Probe.Contract]));
        var seen = await AgentTurnStartFacts.UntilEnd(signals, first, ct);
        var second = await agent.Start(AgentTurn.Text("fail with the key", runId: "turn-2", contracts: [Probe.Contract]));
        seen.AddRange(await AgentTurnStartFacts.UntilEnd(signals, second, ct));

        Assert.Contains(seen.OfType<TurnProgressed>(), step => step.Kind == "tool-call");
        Assert.Contains(seen.OfType<TurnProgressed>(), step => step.Kind == "tool-result");
        Assert.Contains(seen.OfType<TurnProgressed>(), step => step.Kind == "text");
        Assert.Single(seen.OfType<TurnCompleted>());
        foreach (var signal in seen) { Assert.DoesNotContain(Canary, JsonSerializer.Serialize(signal, signal.GetType()), StringComparison.Ordinal); }
        Assert.Contains(SecretRedactor.Mask, string.Concat(seen.OfType<TurnProgressed>().Where(step => step.Kind == "text").Select(step => step.Text)), StringComparison.Ordinal);

        var state = await agent.Read();
        Assert.DoesNotContain(Canary, JsonSerializer.Serialize(state.Runs), StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, JsonSerializer.Serialize(await agent.Transcript()), StringComparison.Ordinal);
        var persisted = new GrainState<AgentStorage>();
        var storage = brain.Services.GetRequiredKeyedService<IGrainStorage>(IntoChatOSNames.ClusterGrainStorage);
        await storage.ReadStateAsync("agent", GrainId.Create("agent", AgentFacts.Key("main")), persisted);
        var exported = JsonSerializer.Serialize(persisted.State);
        Assert.DoesNotContain(Canary, exported, StringComparison.Ordinal);
        Assert.Contains(SecretRedactor.Mask, exported, StringComparison.Ordinal);
    }

    private static Task<ITestBrain> Brain(CancellationToken ct)
        => AgentFacts.Builder(StubChatClient.StreamingOnly(Model))
            .ConfigureServices(services =>
            {
                services.AddSingleton(new CompiledTypes([typeof(INeuron).Assembly, typeof(ILoopProbe).Assembly]));
                services.AddSingleton<ISecretValueSource>(new Vault());
            })
            .StartAsync(ct);

    // Turn one: a tool call carrying the value, then text carrying it split over two parts.
    // Turn two: a tool that fails with a message carrying it.
    private static async IAsyncEnumerable<ChatResponseUpdate> Model(IReadOnlyList<ChatMessage> messages, ChatOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        var asked = messages.Last(message => message.Role == ChatRole.User).Text;
        var answered = messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Any() && asked.StartsWith("echo", StringComparison.Ordinal);
        if (asked.StartsWith("fail", StringComparison.Ordinal))
        {
            yield return new(ChatRole.Assistant, [Probe.Call("fail-call", nameof(ILoopProbe.FailWith), JsonSerializer.Serialize(new[] { "key was " + Canary }))]);
        }
        else if (!answered)
        {
            yield return new(ChatRole.Assistant, [Probe.Call("echo-call", nameof(ILoopProbe.Echo), JsonSerializer.Serialize(new[] { Canary }))]);
        }
        else
        {
            yield return new(ChatRole.Assistant, "the key is " + Canary[..10]);
            yield return new(ChatRole.Assistant, Canary[10..] + " ok");
        }
    }

    private sealed class Vault : ISecretValueSource
    {
        public ValueTask<IReadOnlyList<string>> CurrentValues(CancellationToken cancellationToken) => new([Canary]);
    }
}
