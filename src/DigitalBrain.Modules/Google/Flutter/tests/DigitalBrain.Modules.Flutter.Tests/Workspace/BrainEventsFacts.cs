using System.Collections.Concurrent;
using System.Text.Json;
using DigitalBrain.Apps;
using DigitalBrain.Circles;
using DigitalBrain.Flutter.Workspace;
using DigitalBrain.Framework;
using DigitalBrain.Microsoft.CSharp.Files;
using DigitalBrain.Microsoft.CSharp.Runtime;
using DigitalBrain.Microsoft.CSharp.Runtime.Signals;
using DigitalBrain.Testing.Module;
using Orleans.Metadata;

namespace DigitalBrain.Modules.Flutter.Tests.Workspace;

public sealed class BrainEventsFacts
{
    private static async Task<JsonDocument> Next(IAsyncEnumerator<string> stream, CancellationToken ct)
    {
        Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct));
        return JsonDocument.Parse(stream.Current);
    }

    private static string[] Ids(JsonDocument snapshot)
        => [.. snapshot.RootElement.GetProperty("nodes").EnumerateArray().Select(node => node.GetProperty("id").GetString()!)];

    private static JsonElement[] Synapses(JsonDocument snapshot)
        => [.. snapshot.RootElement.GetProperty("synapses").EnumerateArray()];

    private static string Stamp()
    {
        CallerContextStamper.Stamp(new CallerContext
        {
            PrincipalId = "owner",
            AccountId = "owner",
            BrainId = "owner",
            Kind = CallerKind.User,
            StampedBy = TrustedEdge.AuthenticatedHttp,
        });
        return BrainScope.CurrentId();
    }

    [Fact]
    public async Task ANewNeuronAppearsInTheBrainStream()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<CirclesModule>().StartAsync(ct);
        var scope = Stamp();
        await brain.Self.Create(new Owner("owner"));
        var grown = ICircle.Key(scope, "club");
        await using var stream = BrainEventsEndpoints.Snapshots(brain, scope, ct).GetAsyncEnumerator(ct);

        using var first = await Next(stream, ct);
        Assert.Equal(scope, first.RootElement.GetProperty("rootId").GetString());
        Assert.DoesNotContain(grown, Ids(first));

        await brain.Get<ICircle>(grown).Invite("bob");

        while (true)
        {
            using var next = await Next(stream, ct);
            if (Ids(next).Contains(grown)) { break; }
        }
    }

    [Fact]
    public async Task ASynapseAppearsOnceAnAppsBehaviorSubscribesAndItsAppRuns()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<AppsModule>().WithModule<CirclesModule>().StartAsync(ct);
        var scope = Stamp();
        await brain.Self.Create(new Owner("owner"));
        var appKey = IApp.Key(scope, "watcher");
        await brain.Get<IApp>(appKey).Create(new AppManifest("watcher", "", [new("open", "")], [], []), "spec");
        var publisher = ICircle.Key(scope, "club");
        await using var stream = BrainEventsEndpoints.Snapshots(brain, scope, ct).GetAsyncEnumerator(ct);

        using var first = await Next(stream, ct);
        Assert.Empty(Synapses(first));
        Assert.Equal("Idle", Node(first, appKey).GetProperty("status").GetString());

        var runtime = brain.Get<IBrainRuntime>(BrainRuntimeKeys.For(scope)).AsReference<IStubRuntime>();
        await runtime.Run(appKey + "/synapses/club.cs", $"circles.circle/{publisher}", "CircleSpoke");

        while (true)
        {
            using var next = await Next(stream, ct);
            if (Synapses(next) is not [var synapse]) { continue; }
            Assert.Equal(publisher, synapse.GetProperty("sourceId").GetString());
            Assert.Equal(appKey, synapse.GetProperty("targetId").GetString());
            Assert.Equal("CircleSpoke", synapse.GetProperty("signalType").GetString());
            Assert.Equal("Running", Node(next, appKey).GetProperty("status").GetString());
            break;
        }
    }

    private static JsonElement Node(JsonDocument snapshot, string id)
        => snapshot.RootElement.GetProperty("nodes").EnumerateArray().Single(node => node.GetProperty("id").GetString() == id);
}

[Alias("test.stub-runtime"), DefaultGrainType("test.stub-runtime")]
public interface IStubRuntime : INeuron
{
    Task Run(string behavior, string neuron, string signal);
}

// Stands where the brain runtime would: it answers Inspect with the subscriptions a test hands it and
// publishes BrainRuntimeChanged as the real one does.
[GrainType("microsoft.csharp.runtime")]
public sealed class StubRuntime : Neuron, IBrainRuntime, IStubRuntime
{
    private static readonly ConcurrentDictionary<string, BrainRuntimeSnapshot> Snapshots = new();

    public Task<BrainRuntimeSnapshot> Inspect(CancellationToken cancellationToken = default)
        => Task.FromResult(Snapshots.GetValueOrDefault(this.GetPrimaryKeyString()) ?? new(SessionStatus.None, null, null, null, []));

    public Task Touch(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task Run(string behavior, string neuron, string signal)
    {
        Snapshots[this.GetPrimaryKeyString()] = new(SessionStatus.Connected, "session", null, null,
            [new BehaviorView(behavior, "hash", "Running", null, [new CSharpSubscriptionView(neuron, signal, 0, 0, 0)])]);
        await PublishAsync(new BrainRuntimeChanged(this.GetPrimaryKeyString()));
    }
}
