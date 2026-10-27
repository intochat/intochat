using System.Collections.Concurrent;
using System.Text.Json;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Workspace;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.Microsoft.CSharp.Files;
using IntoChat.OS.Modules.Microsoft.CSharp.Runtime;
using IntoChat.OS.Modules.Microsoft.CSharp.Runtime.Signals;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Workspace;

public sealed class BrainEventsFacts
{
    private static async Task<JsonDocument> Next(IAsyncEnumerator<string> stream, CancellationToken ct)
    {
        Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct));
        return JsonDocument.Parse(stream.Current);
    }

    // The stub publishes the runtime's BrainRuntimeChanged without the CSharp module composed, so its assembly is
    // registered for the journal here.
    private static Task<ITestBrain> Start(CancellationToken ct)
        => TestBrain.Create().WithModule<AppsModule>().WithNeuronModule<StubRuntimeModule>()
            .ConfigureServices(services => global::DigitalBrain.Runtime.Orleans.BrainStorageHosting.AddJournalPayloads(
                services, typeof(IntoChat.OS.Modules.Apps.IApp).Assembly, typeof(BrainRuntimeChanged).Assembly))
            .StartAsync(ct);

    private static string[] Ids(JsonDocument snapshot)
        => [.. snapshot.RootElement.GetProperty("nodes").EnumerateArray().Select(node => node.GetProperty("id").GetString()!)];

    private static JsonElement[] Synapses(JsonDocument snapshot)
        => [.. snapshot.RootElement.GetProperty("synapses").EnumerateArray()];

    [Trait("host", "own")]
    [Fact]
    public async Task ANewNeuronAppearsInTheBrainStream()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        var person = await brain.PersonAsync("growth", ct);
        using var caller = person.AsCaller();
        var scope = person.BrainId;
        var grown = IApp.Key(scope, "club");
        await using var stream = BrainEventsEndpoints.Snapshots(brain, scope, NullLogger.Instance, ct).GetAsyncEnumerator(ct);

        using var first = await Next(stream, ct);
        Assert.Equal(scope, first.RootElement.GetProperty("rootId").GetString());
        Assert.DoesNotContain(grown, Ids(first));

        await brain.Get<IApp>(grown).Create(new AppManifest("club", "", [new("open", "")], [], []), "spec");

        while (true)
        {
            using var next = await Next(stream, ct);
            if (Ids(next).Contains(grown)) { break; }
        }
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ASynapseAppearsOnceAnAppsBehaviorSubscribesAndItsAppRuns()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        var person = await brain.PersonAsync("wiring", ct);
        using var caller = person.AsCaller();
        var scope = person.BrainId;
        var appKey = IApp.Key(scope, "watcher");
        await brain.Get<IApp>(appKey).Create(new AppManifest("watcher", "", [new("open", "")], [], []), "spec");
        var publisher = IApp.Key(scope, "club");
        await using var stream = BrainEventsEndpoints.Snapshots(brain, scope, NullLogger.Instance, ct).GetAsyncEnumerator(ct);

        using var first = await Next(stream, ct);
        Assert.Empty(Synapses(first));
        Assert.Equal("Idle", Node(first, appKey).GetProperty("status").GetString());

        var runtime = brain.Get<IStubRuntime>(BrainRuntimeKeys.For(scope));
        await runtime.Run(appKey + "/synapses/club.cs", $"apps.app2/{publisher}", "AppActivity");

        while (true)
        {
            using var next = await Next(stream, ct);
            if (Synapses(next) is not [var synapse]) { continue; }
            Assert.Equal(publisher, synapse.GetProperty("sourceId").GetString());
            Assert.Equal(appKey, synapse.GetProperty("targetId").GetString());
            Assert.Equal("AppActivity", synapse.GetProperty("signalType").GetString());
            Assert.Equal("Running", Node(next, appKey).GetProperty("status").GetString());
            break;
        }
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ARuntimeThatCannotBeReadKeepsTheLastSynapsesInsteadOfEmittingADegradedSnapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        var person = await brain.PersonAsync("degraded", ct);
        using var caller = person.AsCaller();
        var scope = person.BrainId;
        var appKey = IApp.Key(scope, "steady");
        await brain.Get<IApp>(appKey).Create(new AppManifest("steady", "", [new("open", "")], [], []), "spec");
        var publisher = IApp.Key(scope, "club");
        var runtime = brain.Get<IStubRuntime>(BrainRuntimeKeys.For(scope));
        await runtime.Fail(false);
        await using var stream = BrainEventsEndpoints.Snapshots(brain, scope, NullLogger.Instance, ct).GetAsyncEnumerator(ct);
        using var first = await Next(stream, ct);
        await runtime.Run(appKey + "/synapses/club.cs", $"apps.app2/{publisher}", "AppActivity");
        while (true)
        {
            using var wired = await Next(stream, ct);
            if (Synapses(wired).Length == 1) { break; }
        }

        await runtime.Fail(true);
        await brain.Get<IApp>(publisher).Create(new AppManifest("club", "", [new("open", "")], [], []), "spec");

        while (true)
        {
            using var next = await Next(stream, ct);
            Assert.Single(Synapses(next));
            Assert.Equal("Running", Node(next, appKey).GetProperty("status").GetString());
            if (Ids(next).Contains(publisher)) { break; }
        }
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ARuntimeThatKeepsFailingPastTheTtlStreamsIdleNodesWithoutEdgesAndEdgesReturnWhenItRecoversQuietly()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        var person = await brain.PersonAsync("stale", ct);
        using var caller = person.AsCaller();
        var scope = person.BrainId;
        var appKey = IApp.Key(scope, "steady");
        await brain.Get<IApp>(appKey).Create(new AppManifest("steady", "", [new("open", "")], [], []), "spec");
        var publisher = IApp.Key(scope, "club");
        var runtime = brain.Get<IStubRuntime>(BrainRuntimeKeys.For(scope));
        await runtime.Fail(false);
        await using var stream = BrainEventsEndpoints.Snapshots(brain, scope, NullLogger.Instance, ct, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(200)).GetAsyncEnumerator(ct);
        using var first = await Next(stream, ct);
        await runtime.Run(appKey + "/synapses/club.cs", $"apps.app2/{publisher}", "AppActivity");
        while (true)
        {
            using var wired = await Next(stream, ct);
            if (Synapses(wired).Length == 1) { break; }
        }

        await runtime.Fail(true);

        while (true)
        {
            using var next = await Next(stream, ct);
            if (Synapses(next).Length == 0)
            {
                Assert.Equal("Idle", Node(next, appKey).GetProperty("status").GetString());
                break;
            }
            Assert.Equal("Running", Node(next, appKey).GetProperty("status").GetString());
        }

        await runtime.Fail(false, announce: false);

        while (true)
        {
            using var recovered = await Next(stream, ct);
            if (Synapses(recovered).Length == 1) { break; }
        }
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ABrainWithNoRuntimeStreamsItsNeuronsAndNoSynapses()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        var person = await brain.PersonAsync("alone", ct);
        using var caller = person.AsCaller();
        var scope = person.BrainId;
        var appKey = IApp.Key(scope, "alone");
        await brain.Get<IApp>(appKey).Create(new AppManifest("alone", "", [new("open", "")], [], []), "spec");
        await using var stream = BrainEventsEndpoints.Snapshots(brain, scope, NullLogger.Instance, ct).GetAsyncEnumerator(ct);

        using var first = await Next(stream, ct);

        Assert.Contains(appKey, Ids(first));
        Assert.Empty(Synapses(first));
        Assert.Equal("Idle", Node(first, appKey).GetProperty("status").GetString());
    }

    private static JsonElement Node(JsonDocument snapshot, string id)
        => snapshot.RootElement.GetProperty("nodes").EnumerateArray().Single(node => node.GetProperty("id").GetString() == id);
}

[DigitalBrain.Contracts.NeuronAlias("test.stub-runtime"), DigitalBrain.Contracts.DefaultNeuronType("microsoft.csharp.runtime")]
public interface IStubRuntime
{
    Task Run(string behavior, string neuron, string signal);
    Task Fail(bool failing, bool announce = true);
}

// Stands where the brain runtime would: it answers Inspect with the subscriptions a test hands it and
// publishes BrainRuntimeChanged as the real one does. A plain neuron (06-08) of the runtime's type.
[DigitalBrain.Neuron("microsoft.csharp.runtime")]
public sealed class StubRuntime : DigitalBrain.Neuron, IBrainRuntime, IStubRuntime
{
    private static readonly ConcurrentDictionary<string, BrainRuntimeSnapshot> Snapshots = new();

    private static readonly ConcurrentDictionary<string, bool> Failing = new();

    private string RuntimeId => $"{Id.Brain.Value}/{Id.Key}";

    public Task Fail(bool failing, bool announce = true)
    {
        Failing[RuntimeId] = failing;
        return announce ? Publish(new BrainRuntimeChanged(RuntimeId)) : Task.CompletedTask;
    }

    public Task<BrainRuntimeSnapshot> Inspect(CancellationToken cancellationToken = default)
        => Failing.GetValueOrDefault(RuntimeId)
            ? Task.FromException<BrainRuntimeSnapshot>(new InvalidOperationException("runtime unavailable"))
            : Task.FromResult(Snapshots.GetValueOrDefault(RuntimeId) ?? new(SessionStatus.None, null, null, null, []));

    public Task Touch(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task Run(string behavior, string neuron, string signal)
    {
        Snapshots[RuntimeId] = new(SessionStatus.Connected, "session", null, null,
            [new BehaviorView(behavior, "hash", "Running", null, [new CSharpSubscriptionView(neuron, signal, 0, 0, 0)])]);
        await Publish(new BrainRuntimeChanged(RuntimeId));
    }
}

[DigitalBrain.Module("test.stub-runtime")]
public sealed class StubRuntimeModule : DigitalBrain.Module
{
    public static DigitalBrain.ModuleManifest Manifest { get; } = new(Provides: [], Requires: [], Needs: [], Settings: [], Secrets: []);
}
