using DigitalBrain.Microsoft.CSharp;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests;

public sealed class CSharpFileSnapshotFacts
{
    [Fact]
    public async Task AStartedFileRunsAndListsOneSubscriptionPerStreamedSubscription()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new FakeSandbox();
        await using var brain = await SandboxBrain.StartAsync(sandbox, ct);
        var file = brain.Get<ICSharpFile>("workspace/files/running");
        await file.Write("Console.WriteLine(1);", ct);
        var started = await file.Start(ct);
        Assert.Equal(CSharpFileStatus.Running, started.Status);
        Assert.Empty(started.Subscriptions);

        var pinger = brain.Get<IPinger>("pinger").GetGrainId().ToString();
        await file.AsReference<ICSharpFileEdge>().Subscribed(sandbox.LatestRunId, pinger, nameof(Pinged));

        var inspected = await file.Inspect(ct);
        Assert.Equal([new CSharpSubscriptionView(pinger, nameof(Pinged), 0)], inspected.Subscriptions);
        Assert.Equal("Console.WriteLine(1);", inspected.Source);
    }

    [Fact]
    public async Task AStoppedFileKeepsItsSourceAndIsNoLongerWanted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await SandboxBrain.StartAsync(new FakeSandbox(), ct);
        var file = brain.Get<ICSharpFile>("workspace/files/stopped");
        await file.Write("Console.WriteLine(2);", ct);
        await file.Start(ct);

        var stopped = await file.Stop(ct);

        Assert.False(stopped.ShouldRun);
        Assert.Equal("Console.WriteLine(2);", stopped.Source);
    }

    [Fact]
    public async Task CSharpFileChangedIsPublishedOnWriteStartStopAndDelete()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await SandboxBrain.StartAsync(new FakeSandbox(), ct);
        var file = brain.Get<ICSharpFile>("workspace/files/changes");
        await using var changes = await brain.Observe<CSharpFileChanged>(file, ct);
        await file.Write("Console.WriteLine(4);", ct);
        await file.Start(ct);
        await file.Stop(ct);
        await file.Delete(ct);
        for (var i = 0; i < 4; i++) { Assert.Equal("workspace/files/changes", (await changes.NextAsync(ct: ct)).FileId); }
    }
}
