using DigitalBrain.Microsoft.CSharp;
using DigitalBrain.Microsoft.CSharp.Files;
using DigitalBrain.Microsoft.CSharp.Runtime;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests;

public sealed class CSharpFileNeuronFacts
{
    [Fact]
    public async Task StartCompilesStoresAndAssignsTheBehaviorToTheBrainRuntime()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeSessionHost();
        await using var brain = await RuntimeBrain.StartAsync(host, ct);
        var file = brain.Get<ICSharpFile>("acc-brain/csharp/report");
        await file.Write("Console.WriteLine(\"hi\");", ct);
        await file.Configure(new Dictionary<string, string> { ["TimerId"] = "tea" }, ct);

        var started = await file.Start(ct);

        Assert.Equal(CSharpFileStatus.Starting, started.Status);
        Assert.NotNull(started.Hash);
        Assert.True(started.ShouldRun);
        var view = await Edge(brain).Describe("acc-brain/csharp/report");
        Assert.Equal(started.Hash, view!.Hash);
        Assert.Single(host.Requests, FakeSessionHost.IsStart);
        var assignment = Assert.Single(await Edge(brain).Assignments((await brain.Get<IBrainRuntime>(BrainRuntimeKeys.For("acc-brain")).Inspect(ct)).SessionId!));
        Assert.Equal("tea", assignment.Settings["TimerId"]);
    }

    [Fact]
    public async Task ASourceThatDoesNotCompileIsRefusedBeforeAnythingIsAssigned()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeSessionHost();
        await using var brain = await RuntimeBrain.StartAsync(host, ct);
        var file = brain.Get<ICSharpFile>("acc-brain/csharp/bad");
        await file.Write("int x = \"no\";", ct);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => file.Start(ct));

        Assert.Contains("CS0029", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(host.Requests, FakeSessionHost.IsStart);
    }

    [Fact]
    public async Task SwapReassignsWithANewHashAndDeleteRetires()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeSessionHost();
        await using var brain = await RuntimeBrain.StartAsync(host, ct);
        var file = brain.Get<ICSharpFile>("acc-brain/csharp/swap");
        await file.Write("Console.WriteLine(1);", ct);
        var first = await file.Start(ct);

        var second = await file.Swap("Console.WriteLine(2);", ct);

        Assert.NotEqual(first.Hash, second.Hash);
        Assert.Equal(second.Hash, (await Edge(brain).Describe("acc-brain/csharp/swap"))!.Hash);
        await file.Delete(ct);
        Assert.Null(await Edge(brain).Describe("acc-brain/csharp/swap"));
        Assert.Equal("", (await file.Inspect(ct)).Source);
    }

    [Fact]
    public async Task StopRetiresTheBehaviorButKeepsItsSource()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        var file = brain.Get<ICSharpFile>("acc-brain/csharp/stopped");
        await file.Write("Console.WriteLine(2);", ct);
        await file.Start(ct);

        var stopped = await file.Stop(ct);

        Assert.False(stopped.ShouldRun);
        Assert.Equal(CSharpFileStatus.Stopped, stopped.Status);
        Assert.Equal("Console.WriteLine(2);", stopped.Source);
        Assert.Null(await Edge(brain).Describe("acc-brain/csharp/stopped"));
    }

    [Fact]
    public async Task CallersCannotBindAFileToAnInstalledApp()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => brain.Get<ICSharpAppBinding>("acc-brain/csharp/file").BindApp("arbitrary-app"));
    }

    [Fact]
    public async Task RejectsStartWithoutSourceAndInvalidInput()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        var file = brain.Get<ICSharpFile>("acc-brain/csharp/empty");

        await Assert.ThrowsAsync<InvalidOperationException>(() => file.Start(ct));
        await Assert.ThrowsAsync<ArgumentException>(() => file.Configure(new Dictionary<string, string> { ["bad name"] = "x" }, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => file.Write(new string('x', CSharpFileNeuron.MaximumSourceBytes + 1), ct));
    }

    private static IBrainRuntimeEdge Edge(ModuleBrain brain) => brain.Grains().GetGrain<IBrainRuntimeEdge>(BrainRuntimeKeys.For("acc-brain"));
}
