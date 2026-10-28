using System.Collections.Concurrent;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Connections.Contracts;
using IntoChat.OS.Connections.Surface;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using DigitalBrain.Runtime.Orleans.Hosting;

namespace IntoChat.OS.Modules.AI.Tests.Integration.Agents;

// A contract the turn loop's facts compose, so a model action reaches a real neuron that echoes,
// waits, fails or answers with more than a model should keep.
[DigitalBrain.Contracts.NeuronAlias("test.loop-probe"), DigitalBrain.Contracts.DefaultNeuronType(LoopProbe.Type)]
public interface ILoopProbe
{
    Task<string> Echo(string word);
    Task<string> Wait(CancellationToken ct);
    Task<string> Fail();
    Task<string> FailWith(string message);
    Task<string> Dump();
    Task<string> Conversation();
    Task<string> SeenByAgent(string agent);
}

// A plain neuron (06-08), hosted by LoopProbeNeurons.
[DigitalBrain.Neuron(Type)]
public sealed class LoopProbe : DigitalBrain.Neuron, ILoopProbe
{
    public const string Type = "test-loop-probe";
    public const int DumpCharacters = 300_000;
    public static readonly ConcurrentDictionary<string, TaskCompletionSource> Entered = new();

    public Task<string> Echo(string word) => Task.FromResult("echo:" + word);

    public async Task<string> Wait(CancellationToken ct)
    {
        Entered.GetOrAdd($"{Id.Brain.Value}/{Id.Key}", _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        return "done";
    }

    public async Task<string> Fail() { await Task.Yield(); throw new IOException("Tool failed after starting"); }

    public async Task<string> FailWith(string message) { await Task.Yield(); throw new IOException(message); }

    public Task<string> Dump() => Task.FromResult(new string('r', DumpCharacters));

    public Task<string> Conversation() => Task.FromResult(CallerContextStamper.TryGet(out var caller) ? caller.ConversationId ?? "unmarked" : "unstamped");

    // The agent must have recorded the call before the neuron it reaches runs.
    public async Task<string> SeenByAgent(string agent)
    {
        var run = (await SiloNeurons.Get<IAgent>(Context.Services, agent).Read()).ActiveRun;
        return run?.ToolCalls.Any(item => item.Name == NeuronSurfaceTools.CallName) == true ? "recorded" : "unrecorded";
    }
}

[DigitalBrain.Module("test.loop-probe")]
public sealed class LoopProbeNeurons : DigitalBrain.Module
{
    public static DigitalBrain.ModuleManifest Manifest { get; } = new(Provides: [], Requires: [], Needs: [], Settings: [], Secrets: []);
}

internal static class Probe
{
    public static readonly string Contract = typeof(ILoopProbe).FullName!;

    public static FunctionCallContent Call(string callId, string method, string arguments = "[]", string key = AgentFacts.Scope + "/probes/main")
        => new(callId, NeuronSurfaceTools.CallName, new Dictionary<string, object?>
        { ["contract"] = Contract, ["key"] = key, ["method"] = method, ["arguments"] = arguments });

    // The probe's contract joins what the turns may compose: the host's compiled layer carries the
    // test assembly too, which has no module of its own to register it.
    public static Task<ITestBrain> Brain(IChatClient model, CancellationToken ct)
        => AgentFacts.Builder(model).WithNeuronModule<LoopProbeNeurons>()
            .ConfigureServices(services => services.AddSingleton(new IntoChat.OS.Framework.CompiledTypes([typeof(IntoChat.OS.IBrain).Assembly, typeof(ILoopProbe).Assembly])))
            .StartAsync(ct);
}
