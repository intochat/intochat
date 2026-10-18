using System.Runtime.CompilerServices;
using IntoChat.OS.Client.Runtime;
using IntoChat.OS.Microsoft.CSharp.Catalog;
using IntoChat.OS.Microsoft.CSharp.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Tests.Integration;

public sealed class BehaviorLoaderFacts
{
    [Fact]
    public async Task ALoadedBehaviorRunsItsEntryPointAndUnloads()
    {
        var ct = TestContext.Current.CancellationToken;
        var edge = new FakeBrainEdge();
        var compiled = Compile("Console.WriteLine(\"ran\");\nawait Task.CompletedTask;\n");
        edge.Serve(compiled);
        await using var session = await Open(edge, ct);

        var unloaded = await RunAndUnloadAsync(Loader(edge), session, edge.Assignment("acc-brain/csharp/a", compiled), ct);

        // Unloading finishes after the run's last thread-pool continuation settles, so collection is observed over cycles.
        for (var attempt = 0; attempt < 50 && unloaded.IsAlive; attempt++) { GC.Collect(); GC.WaitForPendingFinalizers(); await Task.Yield(); }
        Assert.False(unloaded.IsAlive, "the behavior's load context must be collectible once its run is disposed");
    }

    [Fact]
    public async Task AThrowingBehaviorCompletesWithOneAndItsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var edge = new FakeBrainEdge();
        var compiled = Compile("throw new InvalidOperationException(\"boom\");\n");
        edge.Serve(compiled);
        await using var session = await Open(edge, ct);

        await using var run = await Loader(edge).LoadAsync(session, edge.Assignment("acc-brain/csharp/b", compiled), NullLogger.Instance, ct);

        Assert.Equal(1, await run.Completion);
        Assert.Contains("boom", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABehaviorReadsItsSettingsThroughTheAmbientSession()
    {
        var ct = TestContext.Current.CancellationToken;
        var edge = new FakeBrainEdge();
        var compiled = Compile("var brain = await IntoChatOSClient.ConnectAsync(args);\nConsole.WriteLine(\"timer \" + brain.Setting(\"TimerId\"));\n");
        edge.Serve(compiled);
        await using var session = await Open(edge, ct);

        await using var run = await Loader(edge).LoadAsync(session, edge.Assignment("acc-brain/csharp/c", compiled, new() { ["TimerId"] = "tea" }), NullLogger.Instance, ct);

        Assert.Equal(0, await run.Completion);
        Assert.Contains("timer tea", run.Tail(10), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAssemblyWhoseBytesDoNotMatchItsHashIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var edge = new FakeBrainEdge();
        var compiled = Compile("Console.WriteLine(1);\n");
        edge.Serve(compiled with { Assembly = Compile("Console.WriteLine(2);\n").Assembly });
        await using var session = await Open(edge, ct);

        await Assert.ThrowsAsync<InvalidDataException>(() => Loader(edge).LoadAsync(session, edge.Assignment("acc-brain/csharp/d", compiled), NullLogger.Instance, ct));
    }

    // Kept out of line so no local keeps the run, its assembly or its context reachable.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> RunAndUnloadAsync(BehaviorLoader loader, SessionConnection session, IntoChat.OS.Client.Runtime.V1.AssignmentEvent assignment, CancellationToken ct)
    {
        IntoChat.OS.Microsoft.CSharp.Sandbox.BehaviorRun? run = await loader.LoadAsync(session, assignment, NullLogger.Instance, ct);
        Assert.Equal(0, await run.Completion);
        Assert.Contains("ran", run.Tail(10), StringComparison.Ordinal);
        var context = new WeakReference(run.Context);
        await run.DisposeAsync();
        // The completed state machine stays reachable through the awaited task; it must not hold the run.
        run = null;
        return context;
    }

    internal static CompiledBehavior Compile(string source)
        => new CSharpScriptCheck().Compile(new Dictionary<string, string> { ["behavior.cs"] = source }).Assemblies["behavior.cs"];

    internal static Task<SessionConnection> Open(FakeBrainEdge edge, CancellationToken ct)
        => SessionConnection.OpenAsync(new Uri(FakeBrainEdge.Edge), "token", "fingerprint", edge, ct);

    internal static BehaviorLoader Loader(FakeBrainEdge edge)
        => new(new HttpClient(edge), Options.Create(new SandboxOptions { CacheRoot = Path.Combine(Path.GetTempPath(), "behaviors-" + Guid.NewGuid().ToString("N")) }));
}
