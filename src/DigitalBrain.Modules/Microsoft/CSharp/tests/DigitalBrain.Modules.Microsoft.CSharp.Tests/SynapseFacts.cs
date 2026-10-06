using DigitalBrain.Microsoft.CSharp;
using Orleans.Runtime;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests;

public sealed class SynapseFacts
{
    [Fact]
    public async Task AStartedSynapseIsAliveAndListsOneWatchPerStreamedSubscription()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new FakeSandbox();
        await using var brain = await SandboxBrain.StartAsync(sandbox, ct);
        var file = brain.Get<ICSharpFile>("workspace/synapses/alive");
        await file.Write("Console.WriteLine(1);", ct);
        var started = await file.Start(ct);
        Assert.True(started.Alive);
        Assert.Empty(started.Watches);

        var pinger = brain.Get<IPinger>("pinger").GetGrainId().ToString();
        await file.AsReference<ICSharpFileEdge>().Subscribed(sandbox.LatestRunId, pinger, nameof(Pinged));
        var synapse = await file.Read(ct);
        Assert.Equal([new SynapseWatch(pinger, nameof(Pinged), 0)], synapse.Watches);
        Assert.Equal("Console.WriteLine(1);", synapse.Program);
        Assert.Null(synapse.LastError);
    }

    [Fact]
    public async Task AStoppedSynapseKeepsItsProgramAndIsNotAlive()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await SandboxBrain.StartAsync(new FakeSandbox(), ct);
        var file = brain.Get<ICSharpFile>("workspace/synapses/stopped");
        await file.Write("Console.WriteLine(2);", ct);
        await file.Start(ct);
        var stopped = await file.Stop(ct);
        Assert.False(stopped.Alive);
        Assert.Equal("Console.WriteLine(2);", stopped.Program);
    }

    [Fact]
    public async Task AnArmedSynapseListsItsTriggerAsAWatchWhileWaiting()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await SandboxBrain.StartAsync(new FakeSandbox(), ct);
        var file = brain.Get<ICSharpFile>("workspace/synapses/armed");
        await file.Write("Console.WriteLine(3);", ct);
        var pinger = brain.Get<IPinger>("pinger").GetGrainId().ToString();
        await file.Arm(new CSharpTrigger(pinger, nameof(Pinged)), ct);
        var synapse = await file.Read(ct);
        Assert.False(synapse.Alive);
        Assert.Equal([new SynapseWatch(pinger, nameof(Pinged), 0)], synapse.Watches);
    }

    [Fact]
    public async Task SynapseChangedIsPublishedOnWriteStartStopAndDelete()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await SandboxBrain.StartAsync(new FakeSandbox(), ct);
        var file = brain.Get<ICSharpFile>("workspace/synapses/changes");
        await using var changes = await brain.Observe<SynapseChanged>(file, ct);
        await file.Write("Console.WriteLine(4);", ct);
        await file.Start(ct);
        await file.Stop(ct);
        await file.Delete(ct);
        for (var i = 0; i < 4; i++) { Assert.Equal("workspace/synapses/changes", (await changes.NextAsync(ct: ct)).SynapseId); }
    }
}
