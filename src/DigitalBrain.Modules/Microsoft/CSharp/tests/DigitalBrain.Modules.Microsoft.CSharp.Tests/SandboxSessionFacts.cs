using DigitalBrain.Client.Runtime.V1;
using DigitalBrain.Microsoft.CSharp.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests;

public sealed class SandboxSessionFacts
{
    [Fact(Timeout = 60_000)]
    public async Task AnAssignEventLoadsAndARetireEventUnloads()
    {
        var ct = TestContext.Current.CancellationToken;
        var edge = new FakeBrainEdge();
        var compiled = BehaviorLoaderFacts.Compile("var brain = await DigitalBrainClient.ConnectAsync(args);\nawait Task.Delay(Timeout.Infinite, brain.Stopping);\n");
        edge.Serve(compiled);
        await using var connection = await BehaviorLoaderFacts.Open(edge, ct);
        await using var session = new Session(connection, BehaviorLoaderFacts.Loader(edge), NullLogger.Instance);
        var control = session.RunAsync(ct);

        edge.Push(AssignmentEvent.Kind, edge.Assignment("acc-brain/csharp/a", compiled));
        Assert.Equal("Running", (await edge.StatusAsync("acc-brain/csharp/a", ct))["status"]!.GetValue<string>());
        Assert.Contains("acc-brain/csharp/a", session.Behaviors.Keys);

        edge.Push(RetireEvent.Kind, new RetireEvent("acc-brain/csharp/a"));
        var stopped = await edge.StatusAsync("acc-brain/csharp/a", ct);

        Assert.Equal(("Stopped", 0), (stopped["status"]!.GetValue<string>(), stopped["exitCode"]!.GetValue<int>()));
        Assert.DoesNotContain("acc-brain/csharp/a", session.Behaviors.Keys);
        Assert.False(control.IsCompleted);
    }

    [Fact(Timeout = 60_000)]
    public async Task AnExitedBehaviorIsReportedWithItsExitCode()
    {
        var ct = TestContext.Current.CancellationToken;
        var edge = new FakeBrainEdge();
        var compiled = BehaviorLoaderFacts.Compile("throw new InvalidOperationException(\"boom\");\n");
        edge.Serve(compiled);
        await using var connection = await BehaviorLoaderFacts.Open(edge, ct);
        await using var session = new Session(connection, BehaviorLoaderFacts.Loader(edge), NullLogger.Instance);
        _ = session.RunAsync(ct);

        edge.Push(AssignmentEvent.Kind, edge.Assignment("acc-brain/csharp/b", compiled));
        Assert.Equal("Running", (await edge.StatusAsync("acc-brain/csharp/b", ct))["status"]!.GetValue<string>());
        var exited = await edge.StatusAsync("acc-brain/csharp/b", ct);

        Assert.Equal(("Exited", 1), (exited["status"]!.GetValue<string>(), exited["exitCode"]!.GetValue<int>()));
        Assert.Contains("boom", exited["error"]!.GetValue<string>(), StringComparison.Ordinal);
    }
}
