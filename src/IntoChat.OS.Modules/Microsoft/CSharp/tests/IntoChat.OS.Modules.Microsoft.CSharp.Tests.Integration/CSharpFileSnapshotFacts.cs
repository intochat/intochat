using IntoChat.OS.Microsoft.CSharp.Files;
using IntoChat.OS.Microsoft.CSharp.Files.Signals;
using IntoChat.OS.Microsoft.CSharp.Runtime;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Tests.Integration;

public sealed class CSharpFileSnapshotFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task AFileReportsTheStatusItsBrainRuntimeLastHeard()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        var file = brain.Get<ICSharpFile>("acc-brain/csharp/running");
        await file.Write("Console.WriteLine(1);", ct);
        var started = await file.Start(ct);
        var session = (await brain.Get<IBrainRuntime>(BrainRuntimeKeys.For("acc-brain")).Inspect(ct)).SessionId!;

        await RuntimeBrain.Edge(brain, "acc-brain").Reported(session, new("acc-brain/csharp/running", started.Hash!, "Running", null, null));

        Assert.Equal("Running", (await file.Inspect(ct)).Status);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task CSharpFileChangedIsPublishedOnWriteStartStopAndDelete()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        var file = brain.Get<ICSharpFile>("acc-brain/csharp/changes");
        await using var changes = await brain.Observe<CSharpFileChanged>(file, ct);
        await file.Write("Console.WriteLine(4);", ct);
        await file.Start(ct);
        await file.Stop(ct);
        await file.Delete(ct);
        for (var i = 0; i < 4; i++) { Assert.Equal("acc-brain/csharp/changes", (await changes.NextAsync(ct: ct)).FileId); }
    }
}
