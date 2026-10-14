using DigitalBrain.Client.Runtime.V1;
using DigitalBrain.Microsoft.CSharp.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

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

    // An update assigns a new hash while the old one waits out its crash backoff. The pending reload belongs
    // to the replaced program: when it comes due between two crashes of the new hash it must not load the old one.
    [Fact]
    public async Task AReloadPendingForAReplacedHashNeverLoadsItAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var edge = new FakeBrainEdge();
        var replaced = BehaviorLoaderFacts.Compile("throw new InvalidOperationException(\"old\");\n");
        // Crashes a little later than the old hash did, so its reload comes due after the old one's.
        var current = BehaviorLoaderFacts.Compile("await Task.Delay(100);\nthrow new InvalidOperationException(\"new\");\n");
        edge.Serve(replaced);
        edge.Serve(current);
        await using var connection = await BehaviorLoaderFacts.Open(edge, ct);
        await using var session = new Session(connection, BehaviorLoaderFacts.Loader(edge), NullLogger.Instance, TimeSpan.FromMilliseconds(500));
        _ = session.RunAsync(ct);
        const string behavior = "acc-brain/apps/demo/run-on-row.cs";

        edge.Push(AssignmentEvent.Kind, edge.Assignment(behavior, replaced));
        await edge.StatusAsync(behavior, ct, status: "Exited");
        edge.Push(AssignmentEvent.Kind, edge.Assignment(behavior, current));
        var crashed = await edge.StatusAsync(behavior, ct, status: "Exited");
        var reloaded = await edge.StatusAsync(behavior, ct, status: "Running");

        Assert.Equal(current.Hash, crashed["hash"]!.GetValue<string>());
        Assert.Equal(current.Hash, reloaded["hash"]!.GetValue<string>());
    }

    // Ready names the brain's whole desired set: a crashed behavior waiting out its backoff is held too,
    // so one the brain no longer names is answered as stopped and never reloaded.
    [Fact]
    public async Task AReadyThatNoLongerNamesACrashedBehaviorCancelsItsReload()
    {
        var ct = TestContext.Current.CancellationToken;
        var edge = new FakeBrainEdge();
        var time = new RetryTime();
        var crashing = BehaviorLoaderFacts.Compile("throw new InvalidOperationException(\"boom\");\n");
        edge.Serve(crashing);
        await using var connection = await BehaviorLoaderFacts.Open(edge, ct);
        await using var session = new Session(connection, BehaviorLoaderFacts.Loader(edge), NullLogger.Instance, TimeSpan.FromMilliseconds(100), time);
        _ = session.RunAsync(ct);
        const string behavior = "acc-brain/apps/demo/run-on-row-deleted.cs";

        edge.Push(AssignmentEvent.Kind, edge.Assignment(behavior, crashing));
        await edge.StatusAsync(behavior, ct, status: "Exited");
        // The retry is pending, but cannot race the Ready event while a contended test thread resumes.
        await time.Scheduled.Task.WaitAsync(ct);
        edge.Push(ReadyEvent.Kind, new ReadyEvent("s1", ["acc-brain/csharp/other"]));
        var next = await edge.StatusAsync(behavior, ct);
        var reportsAfterReady = edge.RecordedReports.Count;
        time.Clock.Advance(TimeSpan.FromMilliseconds(300));
        await Task.Delay(300, ct);

        Assert.Equal(("Stopped", crashing.Hash), (next["status"]!.GetValue<string>(), next["hash"]!.GetValue<string>()));
        Assert.Equal(reportsAfterReady, edge.RecordedReports.Count);   // no reload after the ready
    }

    private sealed class RetryTime : TimeProvider
    {
        public FakeTimeProvider Clock { get; } = new();
        public TaskCompletionSource Scheduled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = Clock.CreateTimer(callback, state, dueTime, period);
            Scheduled.TrySetResult();
            return timer;
        }
    }
}
