using System.Collections.Concurrent;
using DigitalBrain.AI.Agents;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Contracts;
using DigitalBrain.Framework.Surface;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.AI.Tests.Agents;

// A contract the turn loop's facts compose, so a model action reaches a real neuron that echoes,
// waits, fails or answers with more than a model should keep.
[Alias("test.loop-probe")]
public interface ILoopProbe : INeuron
{
    Task<string> Echo(string word);
    Task<string> Wait(CancellationToken ct);
    Task<string> Fail();
    Task<string> Dump();
    Task<string> Conversation();
    Task<string> SeenByAgent(string agent);
}

[GrainType("test-loop-probe")]
public sealed class LoopProbe : Neuron, ILoopProbe
{
    public const int DumpCharacters = 300_000;
    public static readonly ConcurrentDictionary<string, TaskCompletionSource> Entered = new();

    public Task<string> Echo(string word) => Task.FromResult("echo:" + word);

    public async Task<string> Wait(CancellationToken ct)
    {
        Entered.GetOrAdd(this.GetPrimaryKeyString(), _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        return "done";
    }

    public async Task<string> Fail() { await Task.Yield(); throw new IOException("Tool failed after starting"); }

    public Task<string> Dump() => Task.FromResult(new string('r', DumpCharacters));

    public Task<string> Conversation() => Task.FromResult(CallerContextStamper.TryGet(out var caller) ? caller.ConversationId ?? "unmarked" : "unstamped");

    // The agent must have recorded the call before the neuron it reaches runs.
    public async Task<string> SeenByAgent(string agent)
    {
        var run = (await GrainFactory.GetGrain<IAgent>(agent).Read()).ActiveRun;
        return run?.ToolCalls.Any(item => item.Name == NeuronSurfaceTools.CallName) == true ? "recorded" : "unrecorded";
    }
}

internal static class Probe
{
    public static readonly string Contract = typeof(ILoopProbe).FullName!;

    public static FunctionCallContent Call(string callId, string method, string arguments = "[]", string key = AgentFacts.Scope + "/probes/main")
        => new(callId, NeuronSurfaceTools.CallName, new Dictionary<string, object?>
        { ["contract"] = Contract, ["key"] = key, ["method"] = method, ["arguments"] = arguments });

    // The probe's contract joins what the turns may compose. The caller is stamped here, outside any
    // async method, so the stamp stays on the fact's own flow and actions run as someone.
    public static Task<ModuleBrain> Brain(IChatClient client, CancellationToken ct)
    {
        AgentFacts.StampUser();
        return AgentFacts.Brain(client, ct, services => services.AddSingleton(new ContractCatalog([typeof(INeuron).Assembly, typeof(ILoopProbe).Assembly])));
    }
}
