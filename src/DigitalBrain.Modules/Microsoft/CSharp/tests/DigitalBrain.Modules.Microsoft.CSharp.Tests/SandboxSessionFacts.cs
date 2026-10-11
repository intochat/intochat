using DigitalBrain.Client.Runtime.V1;
using DigitalBrain.Microsoft.CSharp.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests;

public sealed class SandboxSessionFacts
{
    private const string Idle = "var brain = await DigitalBrainClient.ConnectAsync(args);\nawait Task.Delay(Timeout.Infinite, brain.Stopping);\n";

    [Fact]
    public async Task EnsuringAnExistingSessionRefreshesItsTokenWithoutReconnecting()
    {
        var ct = TestContext.Current.CancellationToken;
        var edge = new FakeBrainEdge();
        var compiled = BehaviorLoaderFacts.Compile(Idle);
        edge.Serve(compiled);
        var sessions = new Sessions(BehaviorLoaderFacts.Loader(edge), NullLoggerFactory.Instance, edge);
        try
        {
            await sessions.StartAsync("b-1", new SessionStart("acc-brain", "s1", "old-token", FakeBrainEdge.Edge), ct);
            edge.Push(AssignmentEvent.Kind, edge.Assignment("acc-brain/csharp/a", compiled));
            await edge.StatusAsync("acc-brain/csharp/a", ct);

            await sessions.StartAsync("b-1", new SessionStart("acc-brain", "s1", "new-token", FakeBrainEdge.Edge), ct);
            edge.Push(AssignmentEvent.Kind, edge.Assignment("acc-brain/csharp/b", compiled));
            await edge.StatusAsync("acc-brain/csharp/b", ct);

            Assert.Equal("new-token", edge.RecordedReports.Last(item => item.Body["status"]?["behavior"]?.GetValue<string>() == "acc-brain/csharp/b").Token);
            Assert.Single(edge.RecordedReports, item => item.Body["fingerprint"] is not null);
        }
        finally { await sessions.StopAsync("b-1"); }
    }

    [Fact]
    public async Task ANewTokenDuringConnectionIsUsedWhenTheOldTokenIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var edge = new FakeBrainEdge { HoldReports = true, RequiredToken = "new-token" };
        var compiled = BehaviorLoaderFacts.Compile(Idle);
        edge.Serve(compiled);
        var sessions = new Sessions(BehaviorLoaderFacts.Loader(edge), NullLoggerFactory.Instance, edge);
        try
        {
            await sessions.StartAsync("b-1", new SessionStart("acc-brain", "s1", "old-token", FakeBrainEdge.Edge), ct);
            await sessions.StartAsync("b-1", new SessionStart("acc-brain", "s1", "new-token", FakeBrainEdge.Edge), ct);
            edge.Busy.SetResult();
            edge.Push(AssignmentEvent.Kind, edge.Assignment("acc-brain/csharp/a", compiled));
            await edge.StatusAsync("acc-brain/csharp/a", ct);
            Assert.All(edge.RecordedReports, report => Assert.Equal("new-token", report.Token));
        }
        finally { edge.Busy.TrySetResult(); await sessions.StopAsync("b-1"); }
    }

    [Fact]
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

    // A reconnecting stream resends every assignment; one the host already runs at that hash keeps running.
    [Fact]
    public async Task AResentAssignmentOfTheRunningHashLeavesItRunning()
    {
        var ct = TestContext.Current.CancellationToken;
        var edge = new FakeBrainEdge();
        var compiled = BehaviorLoaderFacts.Compile(Idle);
        edge.Serve(compiled);
        await using var connection = await BehaviorLoaderFacts.Open(edge, ct);
        await using var session = new Session(connection, BehaviorLoaderFacts.Loader(edge), NullLogger.Instance);
        _ = session.RunAsync(ct);
        edge.Push(AssignmentEvent.Kind, edge.Assignment("acc-brain/csharp/a", compiled));
        await edge.StatusAsync("acc-brain/csharp/a", ct);
        var running = session.Behaviors["acc-brain/csharp/a"];

        edge.Push(AssignmentEvent.Kind, edge.Assignment("acc-brain/csharp/a", compiled));
        edge.Push(AssignmentEvent.Kind, edge.Assignment("acc-brain/csharp/b", compiled));
        await edge.StatusAsync("acc-brain/csharp/b", ct);

        Assert.Same(running, session.Behaviors["acc-brain/csharp/a"]);
    }

    // Ready names the brain's whole desired set: what the host holds beyond it was retired while the stream was down.
    [Fact]
    public async Task AReadyUnloadsWhatTheBrainNoLongerWants()
    {
        var ct = TestContext.Current.CancellationToken;
        var edge = new FakeBrainEdge();
        var compiled = BehaviorLoaderFacts.Compile(Idle);
        edge.Serve(compiled);
        await using var connection = await BehaviorLoaderFacts.Open(edge, ct);
        await using var session = new Session(connection, BehaviorLoaderFacts.Loader(edge), NullLogger.Instance);
        _ = session.RunAsync(ct);
        edge.Push(AssignmentEvent.Kind, edge.Assignment("acc-brain/csharp/a", compiled));
        await edge.StatusAsync("acc-brain/csharp/a", ct);

        edge.Push(ReadyEvent.Kind, new ReadyEvent("s1", ["acc-brain/csharp/other"]));
        var stopped = await edge.StatusAsync("acc-brain/csharp/a", ct);

        Assert.Equal("Stopped", stopped["status"]!.GetValue<string>());
        Assert.DoesNotContain("acc-brain/csharp/a", session.Behaviors.Keys);
    }

    // The brain starts a session from inside a grain turn; the host dialing back before answering would
    // wait on that very turn. A start is accepted at once and the session connects on its own.
    [Fact]
    public async Task AStartIsAcceptedBeforeTheSessionDialsBack()
    {
        var ct = TestContext.Current.CancellationToken;
        var edge = new FakeBrainEdge { HoldReports = true };
        var sessions = new Sessions(BehaviorLoaderFacts.Loader(edge), NullLoggerFactory.Instance, edge);

        await sessions.StartAsync("b-1", new SessionStart("acc-brain", "s1", "token", FakeBrainEdge.Edge), ct);

        Assert.True(sessions.Contains("b-1"));
        edge.Busy.SetResult();
        await sessions.StopAsync("b-1");
    }

    [Fact]
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

    // A behavior the brain did not retire is the host's to keep alive: an exit is reported and the
    // behavior is loaded again after a backoff, for as long as the assignment stands.
    [Fact]
    public async Task AnExitedBehaviorIsReloadedUntilItIsRetired()
    {
        var ct = TestContext.Current.CancellationToken;
        var edge = new FakeBrainEdge();
        var crashing = BehaviorLoaderFacts.Compile("var brain = await DigitalBrainClient.ConnectAsync(args);\nthrow new InvalidOperationException(\"boom\");\n");
        edge.Serve(crashing);
        await using var connection = await BehaviorLoaderFacts.Open(edge, ct);
        await using var session = new Session(connection, BehaviorLoaderFacts.Loader(edge), NullLogger.Instance, TimeSpan.FromMilliseconds(50));
        var control = session.RunAsync(ct);

        edge.Push(AssignmentEvent.Kind, edge.Assignment("acc-brain/apps/demo/synapses/a.cs", crashing));
        var first = await edge.StatusAsync("acc-brain/apps/demo/synapses/a.cs", ct, status: "Exited");
        Assert.Equal(1, first["exitCode"]!.GetValue<int>());
        var second = await edge.StatusAsync("acc-brain/apps/demo/synapses/a.cs", ct, status: "Running");   // reloaded
        Assert.NotNull(second);
        await edge.StatusAsync("acc-brain/apps/demo/synapses/a.cs", ct, status: "Exited");

        edge.Push(RetireEvent.Kind, new RetireEvent("acc-brain/apps/demo/synapses/a.cs"));
        await edge.StatusAsync("acc-brain/apps/demo/synapses/a.cs", ct, status: "Stopped");
        var reportsAfterRetire = edge.RecordedReports.Count;
        await Task.Delay(300, ct);
        Assert.Equal(reportsAfterRetire, edge.RecordedReports.Count);   // no reload after retire
        Assert.False(control.IsCompleted);
    }
}
