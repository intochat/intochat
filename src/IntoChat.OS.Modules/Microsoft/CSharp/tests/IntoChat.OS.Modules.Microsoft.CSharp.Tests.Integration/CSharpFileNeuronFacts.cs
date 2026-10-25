using IntoChat.OS.Modules.Microsoft.CSharp.Files;
using IntoChat.OS.Modules.Microsoft.CSharp.Runtime;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Tests.Integration;

public sealed class CSharpFileNeuronFacts
{
    [Trait("host", "own")]
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

        Assert.Equal("Starting", started.Status);
        Assert.NotNull(started.Hash);
        Assert.True(started.ShouldRun);
        var program = await Describe(brain, "acc-brain/csharp/report");
        Assert.Equal(started.Hash, Assert.Single(program!.Behaviors).Hash);
        Assert.Equal("acc-brain/csharp/report", program.Behaviors[0].Id);
        await host.NextAsync(FakeSessionHost.IsStart, ct);
        var assignment = Assert.Single(await Edge(brain).Assignments((await brain.Get<IBrainRuntime>(BrainRuntimeKeys.For("acc-brain")).Inspect(ct)).SessionId!));
        Assert.Equal("tea", assignment.Settings["TimerId"]);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task DescribingANeverWrittenFileIsRefusedAndCreatesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        var file = brain.Get<ICSharpFile>("acc-brain/csharp/ghost");

        await Assert.ThrowsAsync<KeyNotFoundException>(() => file.Describe("Ghost", "haunts", ct));

        Assert.Null((await file.Inspect(ct)).Created);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ANullPurposeKeepsTheCurrentOneAndABlankOneClearsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        var file = brain.Get<ICSharpFile>("acc-brain/csharp/described");
        await file.Write("Console.WriteLine(1);", ct);
        await file.Describe("Report", "Sends the report", ct);

        Assert.Equal("Sends the report", (await file.Describe(null, null, ct)).Purpose);
        Assert.Equal("", (await file.Describe(null, "  ", ct)).Purpose);
    }

    [Trait("host", "own")]
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

    [Trait("host", "own")]
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
        Assert.Equal(second.Hash, (await Describe(brain, "acc-brain/csharp/swap"))!.Behaviors[0].Hash);
        await file.Delete(ct);
        Assert.Null(await Describe(brain, "acc-brain/csharp/swap"));
        Assert.Equal("", (await file.Inspect(ct)).Source);
    }

    [Trait("host", "own")]
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
        Assert.Equal("Stopped", stopped.Status);
        Assert.Equal("Console.WriteLine(2);", stopped.Source);
        Assert.Null(await Describe(brain, "acc-brain/csharp/stopped"));
    }

    [Trait("host", "own")]
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

    private static IBrainRuntimeEdge Edge(ITestBrain brain) => RuntimeBrain.Edge(brain, "acc-brain");

    private static Task<ProgramView?> Describe(ITestBrain brain, string owner)
        => RuntimeBrain.Programs(brain, "acc-brain").Describe(owner);
}
