using System.Collections.Concurrent;
using DigitalBrain.Client.Runtime;
using DigitalBrain.Client.Runtime.V1;
using DigitalBrain.Framework;
using DigitalBrain.Microsoft.CSharp;
using DigitalBrain.Microsoft.CSharp.Runtime;
using DigitalBrain.Microsoft.CSharp.Runtime.Signals;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests.Integration;

public sealed class BrainRuntimeNeuronFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task HttpStartupImmediatelyRetriesDeferredSessions()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var ct = deadline.Token;
        var host = new FakeSessionHost();
        var server = new FakeServer();
        using var lifetime = new RuntimeHostLifetime();
        await using var brain = await RuntimeBrain.Create(host)
            .ConfigureServices(services => services.AddSingleton<IServer>(server)
                .AddKeyedSingleton<IHostApplicationLifetime>(typeof(CSharpModule), lifetime)).StartAsync(ct);
        server.Addresses.Clear();
        await Programs(brain).Assign(Owner, Synapse(), []);
        Assert.Equal(SessionStatus.Starting, (await Runtime(brain).Inspect(ct)).Session);
        server.Addresses.Add("http://127.0.0.1:5099");
        lifetime.Started.Cancel();
        var started = await host.NextAsync(FakeSessionHost.IsStart, ct);
        Assert.Equal("http://host.docker.internal:5099/brains/acc-brain/", started.Body!["edgeUrl"]!.GetValue<string>());
    }

    private sealed class RuntimeHostLifetime : IHostApplicationLifetime, IDisposable
    {
        public CancellationTokenSource Started { get; } = new();
        public CancellationToken ApplicationStarted => Started.Token;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
        public void Dispose() => Started.Dispose();
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ReadinessRequiresCurrentAssignmentInitializationAndDrainedSubscriptions()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        await Programs(brain).Assign(Owner, Synapse(), []);
        var session = (await Runtime(brain).Inspect(ct)).SessionId!;
        var edge = Edge(brain);
        await edge.Connected(session, RuntimeBrain.Fingerprint(brain));
        var assignment = Assert.Single(await edge.Assignments(session));
        await edge.Reported(session, new(Behavior, assignment.Hash, "Initialized", null, null, assignment.Revision));
        Assert.False((await Runtime(brain).Inspect(ct)).IsReady); // No subscription is not an idle persistent script.
        var source = brain.Get<IPinger>("readiness");
        var subscription = await edge.Subscribed(session, Behavior, source.GetGrainId().ToString(), nameof(Pinged));
        Assert.True((await Runtime(brain).Inspect(ct)).IsReady);
        await using var changes = await brain.Observe<BrainRuntimeChanged>(Runtime(brain), ct);
        await source.Ping(1);
        await changes.NextAsync(ct: ct);
        Assert.False((await Runtime(brain).Inspect(ct)).IsReady);
        await edge.Acknowledge(session, subscription, 1);
        Assert.True((await Runtime(brain).Inspect(ct)).IsReady);
        await Programs(brain).Assign(Owner, Synapse(), new() { ["changed"] = "settings" });
        var updated = Assert.Single(await edge.Assignments(session));
        Assert.NotEqual(assignment.Revision, updated.Revision);
        await edge.Reported(session, new(Behavior, assignment.Hash, "Initialized", null, null, assignment.Revision));
        Assert.False((await Runtime(brain).Inspect(ct)).IsReady);
        await edge.Reported(session, new(Behavior, "previous-build-hash", "Initialized", null, null, updated.Revision));
        Assert.False((await Runtime(brain).Inspect(ct)).IsReady);
        await edge.Reported(session, new(Behavior, updated.Hash, "Initialized", null, null, updated.Revision));
        Assert.True((await Runtime(brain).Inspect(ct)).IsReady);
        await Programs(brain).Assign(Owner, Synapse(), []);
        var restored = Assert.Single(await edge.Assignments(session));
        Assert.NotEqual(assignment.Revision, restored.Revision);
        await edge.Reported(session, new(Behavior, assignment.Hash, "Initialized", null, null, assignment.Revision));
        Assert.False((await Runtime(brain).Inspect(ct)).IsReady);
        await edge.Reported(session, new(Behavior, restored.Hash, "Initialized", null, null, restored.Revision));
        Assert.True((await Runtime(brain).Inspect(ct)).IsReady);
        await edge.Reported(session, new(Behavior, assignment.Hash, "Exited", 1, "old process", assignment.Revision));
        Assert.True((await Runtime(brain).Inspect(ct)).IsReady);
        await Programs(brain).Assign(Owner, Synapse(), []);
        Assert.Equal(restored.Revision, Assert.Single(await edge.Assignments(session)).Revision);
        Assert.True((await Runtime(brain).Inspect(ct)).IsReady);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AContinuationHoldsTheTurnUntilTheDetachedWorkSignals()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        var programs = RuntimeBrain.Programs(brain, Brain);
        var runtime = RuntimeBrain.Runtime(brain, Brain);
        var edge = RuntimeBrain.Edge(brain, Brain);
        await programs.Assign(Owner, Synapse(), []);
        var session = (await runtime.Inspect(ct)).SessionId!;
        await edge.Connected(session, RuntimeBrain.Fingerprint(brain));
        var assignment = Assert.Single(await edge.Assignments(session));
        await edge.Reported(session, new(Behavior, assignment.Hash, "Initialized", null, null, assignment.Revision));
        var source = brain.Get<IPinger>("continuations");
        var subscription = await edge.Subscribed(session, Behavior, source.GetGrainId().ToString(), nameof(Pinged));
        Assert.True((await runtime.Inspect(ct)).IsReady);

        var continuations = brain.Services.GetRequiredService<IGrainFactory>().GetGrain<IBrainRuntimeContinuations>(BrainRuntimeKeys.For(Brain));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => continuations.Acquire(" "));
        await continuations.Acquire("route:1");
        await continuations.Acquire("route:1");
        Assert.False((await runtime.Inspect(ct)).IsReady);

        var runs = brain.Get<IBrainRuns>(BrainRunsKeys.For(Brain));
        await using var revisions = await brain.Observe<BrainRunsChanged>(runs, ct);
        await using var changes = await brain.Observe<BrainRuntimeChanged>(runtime, ct);
        await source.Ping(1);
        await changes.NextAsync(ct: ct);
        await edge.Acknowledge(session, subscription, 1);
        while (!(await runs.Read()).Recent.Any(entry => entry.Behavior == Behavior))
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(20, ct);
        }

        using (var quiet = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            quiet.CancelAfter(TimeSpan.FromSeconds(1));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await revisions.NextAsync(ct: quiet.Token));
        }

        await continuations.Release("route:1");
        var changed = await revisions.NextAsync(ct: ct);
        Assert.Contains(Behavior, changed.Behaviors!);
        Assert.True((await runtime.Inspect(ct)).IsReady);
        await continuations.Release("route:1");
        Assert.True((await runtime.Inspect(ct)).IsReady);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ASavedRunAnnouncesAfterTheRunsNeuronWakesAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        var programs = RuntimeBrain.Programs(brain, Brain);
        var runtime = RuntimeBrain.Runtime(brain, Brain);
        var edge = RuntimeBrain.Edge(brain, Brain);
        await programs.Assign(Owner, Synapse(), []);
        var session = (await runtime.Inspect(ct)).SessionId!;
        await edge.Connected(session, RuntimeBrain.Fingerprint(brain));
        var assignment = Assert.Single(await edge.Assignments(session));
        await edge.Reported(session, new(Behavior, assignment.Hash, "Initialized", null, null, assignment.Revision));
        var source = brain.Get<IPinger>("owed-runs");
        var subscription = await edge.Subscribed(session, Behavior, source.GetGrainId().ToString(), nameof(Pinged));
        var continuations = brain.Services.GetRequiredService<IGrainFactory>().GetGrain<IBrainRuntimeContinuations>(BrainRuntimeKeys.For(Brain));
        await continuations.Acquire("route:owed");
        var runs = brain.Get<IBrainRuns>(BrainRunsKeys.For(Brain));
        await using var revisions = await brain.Observe<BrainRunsChanged>(runs, ct);
        await source.Ping(1);
        await edge.Acknowledge(session, subscription, 1);
        while (!(await runs.Read()).Recent.Any(entry => entry.Behavior == Behavior))
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(20, ct);
        }

        await brain.DeactivateAsync(runs, ct);
        await continuations.Release("route:owed");
        var changed = await revisions.NextAsync(ct: ct);
        Assert.Contains(Behavior, changed.Behaviors!);
    }

    private const string Brain = "acc-brain";
    private const string Owner = "acc-brain/apps/x";
    private const string Behavior = "acc-brain/apps/x/synapses/a.cs";

    private static Dictionary<string, string> Synapse(string source = RuntimeBrain.Idle)
        => new(StringComparer.Ordinal) { ["synapses/a.cs"] = source };

    [Trait("host", "own")]
    [Fact]
    public async Task AcknowledgementBatchesPersistIndependentWatermarksAndRejectInvalidBatches()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        var runtime = Edge(brain);
        await Programs(brain).Assign(Owner, Synapse(), []);
        var session = (await Runtime(brain).Inspect(ct)).SessionId!;
        var one = brain.Get<IPinger>("one");
        var two = brain.Get<IPinger>("two");
        var first = await runtime.Subscribed(session, Behavior, one.GetGrainId().ToString(), nameof(Pinged));
        var second = await runtime.Subscribed(session, Behavior, two.GetGrainId().ToString(), nameof(Pinged));
        await using var changes = await brain.Observe<BrainRuntimeChanged>(Runtime(brain), ct);
        await one.Ping(1); await changes.NextAsync(ct: ct);
        await one.Ping(2); await changes.NextAsync(ct: ct);
        await two.Ping(3); await changes.NextAsync(ct: ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => runtime.AcknowledgeMany("stale", [new(first, 2)]));
        foreach (var invalid in new AcknowledgeReport[][] { [], [new(first, 2), null!], [new(first, -1)],
            [new("", 1)], Enumerable.Repeat(new AcknowledgeReport(first, 1), RuntimeProtocol.MaximumAcknowledgements + 1).ToArray() })
        { await Assert.ThrowsAnyAsync<ArgumentException>(() => runtime.AcknowledgeMany(session, invalid)); }
        Assert.Equal(3, (await runtime.PendingBatches(session)).Sum(batch => batch.Signals.Length));
        await runtime.AcknowledgeMany(session, [new(first, 1), new(second, 0), new("retired", 99), new(first, 1)]);
        var pending = await runtime.PendingBatches(session);
        Assert.Equal(2, pending.Count);
        Assert.Equal(2, SignalJson.Number(Assert.Single(pending.Single(batch => batch.Subscription == first).Signals)));
        Assert.Equal(3, SignalJson.Number(Assert.Single(pending.Single(batch => batch.Subscription == second).Signals)));
        await brain.DeactivateAsync(Runtime(brain), ct);
        var resumed = (await Runtime(brain).Inspect(ct)).SessionId!;
        Assert.Equal(2, (await runtime.PendingBatches(resumed)).Sum(batch => batch.Signals.Length));
        await runtime.AcknowledgeMany(resumed, [new(first, 2), new(second, 1)]);
        await brain.DeactivateAsync(Runtime(brain), ct);
        resumed = (await Runtime(brain).Inspect(ct)).SessionId!;
        Assert.Empty(await runtime.PendingBatches(resumed));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AssigningTheFirstBehaviorStartsASessionAndAnEmptySetStopsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeSessionHost();
        await using var brain = await RuntimeBrain.StartAsync(host, ct);

        await Programs(brain).Assign(Owner, Synapse(), []);

        var started = await host.NextAsync(FakeSessionHost.IsStart, ct);
        Assert.Equal(new UriBuilder(brain.HttpClient.BaseAddress!) { Host = "host.docker.internal" }.Uri + "brains/acc-brain/", started.Body!["edgeUrl"]!.GetValue<string>());
        Assert.Equal(SessionStatus.Starting, (await Runtime(brain).Inspect(ct)).Session);
        await Programs(brain).Retire(Owner);
        await host.NextAsync(FakeSessionHost.IsStop, ct);
        Assert.Equal(SessionStatus.None, (await Runtime(brain).Inspect(ct)).Session);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnAbsentSandboxLeavesTheSessionStartingForTheReminderToRetry()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeSessionHost { Absent = true };
        await using var brain = await RuntimeBrain.StartAsync(host, ct);

        await Programs(brain).Assign(Owner, Synapse(), []);

        Assert.Equal(SessionStatus.Starting, (await Runtime(brain).Inspect(ct)).Session);
        Assert.Equal("Starting", (await Programs(brain).Describe(Owner))!.Behaviors[0].Status);
        host.Absent = false;
        await Runtime(brain).AsReference<IRemindable>().ReceiveReminder(BrainRuntimeNeuron.ReconcileReminder, default, ct);
        await host.NextAsync(FakeSessionHost.IsStart, ct);
    }

    // A default app installs while the silo is still starting: its program is assigned, and the session waits
    // for the reminder, because the silo has bound no address a sandbox could dial back yet.
    [Trait("host", "own")]
    [Fact]
    public async Task AnAssignBeforeTheSiloListensDefersTheSessionToTheReminder()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeSessionHost();
        var server = new FakeServer();
        var logs = new RecordingLogs();
        using var lifetime = new RuntimeHostLifetime();
        await using var brain = await RuntimeBrain.Create(host)
            .ConfigureServices(services => services.AddSingleton<IServer>(server).AddSingleton<ILoggerProvider>(logs)
                .AddKeyedSingleton<IHostApplicationLifetime>(typeof(CSharpModule), lifetime))
            .StartAsync(ct);
        // A silo still starting has bound nothing yet.
        server.Addresses.Clear();

        await Programs(brain).Assign(Owner, Synapse(), []);

        Assert.Equal(SessionStatus.Starting, (await Runtime(brain).Inspect(ct)).Session);
        Assert.Contains(logs.Of<BrainRuntimeNeuron>(), entry => entry.Level == LogLevel.Information && entry.Message.Contains("waits until this silo listens", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Of<BrainRuntimeNeuron>(), entry => entry.Level >= LogLevel.Warning);
        server.Addresses.Add("http://127.0.0.1:5099");
        await Runtime(brain).AsReference<IRemindable>().ReceiveReminder(BrainRuntimeNeuron.ReconcileReminder, default, ct);
        var started = await host.NextAsync(FakeSessionHost.IsStart, ct);
        Assert.Equal("http://host.docker.internal:5099/brains/acc-brain/", started.Body!["edgeUrl"]!.GetValue<string>());
        Assert.Single(host.Requests, FakeSessionHost.IsStart);
    }

    // A silo that listens only where no sandbox can dial, with no EdgeUrl configured, never will: the program is
    // still assigned, and the brain says what to configure.
    [Trait("host", "own")]
    [Fact]
    public async Task AnAssignOnASiloWithoutAnHttpAddressReportsTheMissingEdgeUrl()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeSessionHost();
        var logs = new RecordingLogs();
        await using var brain = await RuntimeBrain.Create(host)
            .ConfigureServices(services => services.AddSingleton<IServer>(new FakeServer("https://127.0.0.1:1")).AddSingleton<ILoggerProvider>(logs))
            .StartAsync(ct);

        await Programs(brain).Assign(Owner, Synapse(), []);

        Assert.Equal(SessionStatus.Starting, (await Runtime(brain).Inspect(ct)).Session);
        Assert.Contains(logs.Of<BrainRuntimeNeuron>(), entry => entry.Level == LogLevel.Error
            && entry.Message.Contains($"Configure {CSharpDeploymentSettings.SectionName}:EdgeUrl", StringComparison.Ordinal));
        Assert.DoesNotContain(host.Requests, FakeSessionHost.IsStart);
    }

    // A server whose addresses the fact controls. The host fills an empty list with its configured URL when it
    // starts; a real server's list is empty until it binds, which a fact stands for by emptying it again.
    private sealed class FakeServer : IServer
    {
        private readonly ServerAddressesFeature _addresses = new();

        public FakeServer(params string[] addresses)
        {
            foreach (var address in addresses) { _addresses.Addresses.Add(address); }
            Features.Set<IServerAddressesFeature>(_addresses);
        }

        public IFeatureCollection Features { get; } = new FeatureCollection();

        public ICollection<string> Addresses => _addresses.Addresses;

        public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken) where TContext : notnull
            => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose() { }
    }

    private sealed class RecordingLogs : ILoggerProvider
    {
        private readonly ConcurrentQueue<(string Category, LogLevel Level, string Message)> _entries = new();

        public (string Category, LogLevel Level, string Message)[] Of<T>() => [.. _entries.Where(entry => entry.Category == typeof(T).FullName)];

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _entries);

        public void Dispose() { }

        private sealed class Logger(string category, ConcurrentQueue<(string Category, LogLevel Level, string Message)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => entries.Enqueue((category, logLevel, formatter(state, exception)));
        }
    }

    // Local blob storage listens on the developer's loopback, which a sandbox container cannot reach.
    [Trait("host", "own")]
    [Fact]
    public async Task ALoopbackAssemblyUrlReachesTheHostThroughTheDockerHost()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct, assemblies: new LoopbackAssemblies());
        await Programs(brain).Assign(Owner, Synapse(), []);
        var session = (await Runtime(brain).Inspect(ct)).SessionId!;

        var assignment = Assert.Single(await Edge(brain).Assignments(session));

        Assert.Equal("host.docker.internal", assignment.Assembly.Host);
        Assert.Equal("host.docker.internal", assignment.Symbols.Host);
    }

    private sealed class LoopbackAssemblies : DigitalBrain.Microsoft.CSharp.Assemblies.IBehaviorAssemblies
    {
        public Task Put(DigitalBrain.Microsoft.CSharp.Catalog.CompiledBehavior behavior, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> Exists(string hash, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<Uri> ReadUrl(string name, TimeSpan lifetime, CancellationToken cancellationToken)
            => Task.FromResult(new Uri("http://127.0.0.1:10000/devstoreaccount1/behaviors/" + name + "?sig=x"));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task TouchWithNothingAssignedStartsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeSessionHost();
        await using var brain = await RuntimeBrain.StartAsync(host, ct);

        await Runtime(brain).Touch(ct);

        Assert.DoesNotContain(host.Requests, FakeSessionHost.IsStart);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AMatchingFingerprintConnectsAndAMismatchRefuses()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeSessionHost();
        await using var brain = await RuntimeBrain.StartAsync(host, ct);
        await Programs(brain).Assign(Owner, Synapse(), []);
        var session = (await Runtime(brain).Inspect(ct)).SessionId!;
        var fingerprint = RuntimeBrain.Fingerprint(brain);

        await Edge(brain).Connected(session, fingerprint + "\nExtra.Contracts=00");
        Assert.Equal(SessionStatus.Connected, (await Runtime(brain).Inspect(ct)).Session);

        await Edge(brain).Connected(session, "DigitalBrain=stale");
        Assert.Equal(SessionStatus.Refused, (await Runtime(brain).Inspect(ct)).Session);
        Assert.False((await Edge(brain).Authorize(session)).Active);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AReplacedSessionCannotResolveAStillAssignedBehavior()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new MovableTime();
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct, time: time);
        await Programs(brain).Assign(Owner, Synapse(), []);
        var stale = (await Runtime(brain).Inspect(ct)).SessionId!;
        Assert.NotNull(await Edge(brain).Identity(stale, Behavior));

        time.Now += TimeSpan.FromMinutes(3);
        await Runtime(brain).Touch(ct);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Edge(brain).Identity(stale, Behavior));
        var current = (await Runtime(brain).Inspect(ct)).SessionId!;
        Assert.NotNull(await Edge(brain).Identity(current, Behavior));
        Assert.Null(await Edge(brain).Identity(current, "acc-brain/apps/x/synapses/unassigned.cs"));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ASignalIsBufferedUntilAcknowledgedAndResentOnTheNextStream()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeSessionHost();
        await using var brain = await RuntimeBrain.StartAsync(host, ct);
        await Programs(brain).Assign(Owner, Synapse(), []);
        var session = (await Runtime(brain).Inspect(ct)).SessionId!;
        var pinger = brain.Get<IPinger>("pinger");
        var subscription = await Edge(brain).Subscribed(session, Behavior, pinger.GetGrainId().ToString(), nameof(Pinged));
        await using var changes = await brain.Observe<BrainRuntimeChanged>(Runtime(brain), ct);

        await pinger.Ping(1);
        await changes.NextAsync(ct: ct);

        var pending = Assert.Single(await Edge(brain).PendingBatches(session));
        Assert.Equal((subscription, 1L), (pending.Subscription, pending.Watermark));
        Assert.Equal(1, SignalJson.Number(Assert.Single(pending.Signals)));
        Assert.Single(await Edge(brain).PendingBatches(session));   // reading does not clear it
        await Edge(brain).Acknowledge(session, subscription, 1);
        Assert.Empty(await Edge(brain).PendingBatches(session));
    }

    // Pending is durable: what a previous activation stored reaches the sandbox before what the source publishes next.
    [Trait("host", "own")]
    [Fact]
    public async Task StoredPendingIsPushedBeforeNewlyReadSignals()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        await Programs(brain).Assign(Owner, Synapse(), []);
        var session = (await Runtime(brain).Inspect(ct)).SessionId!;
        var pinger = brain.Get<IPinger>("stored");
        var subscription = await Edge(brain).Subscribed(session, Behavior, pinger.GetGrainId().ToString(), nameof(Pinged));
        await using (var changes = await brain.Observe<BrainRuntimeChanged>(Runtime(brain), ct))
        {
            await pinger.Ping(1);
            await changes.NextAsync(ct: ct);
        }
        await brain.DeactivateAsync(Runtime(brain), ct);
        var resumed = (await Runtime(brain).Inspect(ct)).SessionId!;
        await using var resumedChanges = await brain.Observe<BrainRuntimeChanged>(Runtime(brain), ct);

        await pinger.Ping(2);
        IReadOnlyList<SignalBatchEvent> pending;
        while ((pending = await Edge(brain).PendingBatches(resumed)).Sum(batch => batch.Signals.Length) < 2) { await resumedChanges.NextAsync(ct: ct); }

        var batch = Assert.Single(pending);
        Assert.Equal(subscription, batch.Subscription);
        Assert.Equal([1, 2], batch.Signals.Select(SignalJson.Number).ToArray());
    }

    // An older program's subscription can name a neuron type the host no longer carries; it must not
    // keep every other source from being watched.
    [Trait("host", "own")]
    [Fact]
    public async Task ASourceThatCannotBeWatchedDoesNotStarveTheOthers()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        await Programs(brain).Assign(Owner, Synapse(), []);
        var session = (await Runtime(brain).Inspect(ct)).SessionId!;
        await Assert.ThrowsAnyAsync<Exception>(() => Edge(brain).Subscribed(session, Behavior, "revisions.index/acc-brain", nameof(Pinged)));
        var pinger = brain.Get<IPinger>("pinger");

        var subscription = await Edge(brain).Subscribed(session, Behavior, pinger.GetGrainId().ToString(), nameof(Pinged));
        await using var changes = await brain.Observe<BrainRuntimeChanged>(Runtime(brain), ct);
        await pinger.Ping(1);
        await changes.NextAsync(ct: ct);

        Assert.Equal(subscription, Assert.Single(await Edge(brain).PendingBatches(session)).Subscription);
    }

    // A reloaded behavior subscribes again; what waited for it while it was down is pushed at once, not on the next arrival.
    [Trait("host", "own")]
    [Fact]
    public async Task AResubscribingBehaviorGetsWhatIsPendingForIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        await Programs(brain).Assign(Owner, Synapse(), []);
        var session = (await Runtime(brain).Inspect(ct)).SessionId!;
        await Edge(brain).Connected(session, RuntimeBrain.Fingerprint(brain));
        var pinger = brain.Get<IPinger>("pinger");
        var subscription = await Edge(brain).Subscribed(session, Behavior, pinger.GetGrainId().ToString(), nameof(Pinged));
        await using var changes = await brain.Observe<BrainRuntimeChanged>(Runtime(brain), ct);
        await pinger.Ping(1);
        await changes.NextAsync(ct: ct);
        // Queued behind the arrival's turn: once it answers, the arrival's own push (to no stream) is over.
        Assert.Single(await Edge(brain).PendingBatches(session));
        var stream = brain.Services.GetRequiredService<DigitalBrain.Microsoft.CSharp.Sessions.SessionStreams>().Open(session);

        await Edge(brain).Subscribed(session, Behavior, pinger.GetGrainId().ToString(), nameof(Pinged));

        Assert.True(stream.Reader.TryRead(out var pushed));
        Assert.Equal(DigitalBrain.Client.Runtime.V1.SignalBatchEvent.Kind, pushed.Kind);
        Assert.Equal(subscription, ((DigitalBrain.Client.Runtime.V1.SignalBatchEvent)pushed.Payload).Subscription);
    }

    // The host forgot the session (its container was replaced): the reminder ensures the same session again.
    [Trait("host", "own")]
    [Fact]
    public async Task AReminderEnsuresTheSessionAgainWhenTheHostLostIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeSessionHost();
        await using var brain = await RuntimeBrain.StartAsync(host, ct);
        await Programs(brain).Assign(Owner, Synapse(), []);
        var first = await host.NextAsync(FakeSessionHost.IsStart, ct);
        host.Sessions.Clear();

        await Runtime(brain).AsReference<IRemindable>().ReceiveReminder(BrainRuntimeNeuron.ReconcileReminder, default, ct);

        var again = await host.NextAsync(FakeSessionHost.IsStart, ct);
        Assert.Equal(first.Body!["session"]!.GetValue<string>(), again.Body!["session"]!.GetValue<string>());
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ReactivationRelaunchesBehaviorsWithoutWaitingForAReminder()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeSessionHost();
        var time = new MovableTime();
        await using var brain = await RuntimeBrain.StartAsync(host, ct, time: time);
        await Programs(brain).Assign(Owner, Synapse(), []);
        var first = await host.NextAsync(FakeSessionHost.IsStart, ct);
        var session = (await Runtime(brain).Inspect(ct)).SessionId!;
        await Edge(brain).Connected(session, RuntimeBrain.Fingerprint(brain));
        await brain.DeactivateAsync(Runtime(brain), ct);
        time.Now += TimeSpan.FromSeconds(30);

        var resumed = await Runtime(brain).Inspect(ct);
        var again = await host.NextAsync(FakeSessionHost.IsStart, ct).WaitAsync(TimeSpan.FromSeconds(5), ct);

        Assert.NotEqual(session, resumed.SessionId);
        Assert.Equal(resumed.SessionId, again.Body!["session"]!.GetValue<string>());
        Assert.NotEqual(first.Body!["token"]!.GetValue<string>(), again.Body["token"]!.GetValue<string>());
        Assert.False((await Edge(brain).Authorize(session)).Active);
        Assert.Single(resumed.Behaviors);
    }

    // Heartbeats are the detector: a session silent past the limit is replaced, and its token stops speaking.
    [Trait("host", "own")]
    [Fact]
    public async Task ASilentSessionIsReplacedByANewOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeSessionHost();
        var time = new MovableTime();
        await using var brain = await RuntimeBrain.StartAsync(host, ct, time: time);
        await Programs(brain).Assign(Owner, Synapse(), []);
        await host.NextAsync(FakeSessionHost.IsStart, ct);
        var silent = (await Runtime(brain).Inspect(ct)).SessionId!;

        time.Now += TimeSpan.FromMinutes(3);
        await Runtime(brain).AsReference<IRemindable>().ReceiveReminder(BrainRuntimeNeuron.ReconcileReminder, default, ct);

        var relaunched = await host.NextAsync(FakeSessionHost.IsStart, ct);
        Assert.NotEqual(silent, relaunched.Body!["session"]!.GetValue<string>());
        Assert.False((await Edge(brain).Authorize(silent)).Active);
    }

    // Sandbox calls leave the runtime's turn, but not their order: a stop still in flight must not land
    // after the start that follows it and take the new session down.
    [Trait("host", "own")]
    [Fact]
    public async Task AStopReachesTheHostBeforeTheStartThatFollowsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeSessionHost();
        await using var brain = await RuntimeBrain.StartAsync(host, ct);
        await Programs(brain).Assign("acc-brain/apps/a", Synapse(), []);
        await host.NextAsync(FakeSessionHost.IsStart, ct);
        host.HoldStops = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await Programs(brain).Retire("acc-brain/apps/a");
        await host.NextAsync(FakeSessionHost.IsStop, ct);
        await Programs(brain).Assign("acc-brain/apps/b", Synapse(), []);
        // While the stop is held, the start that follows it must not reach the host: a bounded window that a
        // cancelled read leaves the channel untouched by, so the start still arrives once the stop is released.
        using (var held = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            held.CancelAfter(TimeSpan.FromMilliseconds(200));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.NextAsync(FakeSessionHost.IsStart, held.Token));
        }
        host.HoldStops.SetResult();

        await host.NextAsync(FakeSessionHost.IsStart, ct);
        Assert.False(host.StartDuringStop);
    }

    // A rotation pushed while the host's stream is down never reached it; the next reminder rotates again.
    [Trait("host", "own")]
    [Fact]
    public async Task ATokenRotationTheStreamMissedIsRetried()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeSessionHost();
        var time = new MovableTime();
        await using var brain = await RuntimeBrain.StartAsync(host, ct, time: time);
        await Programs(brain).Assign(Owner, Synapse(), []);
        await host.NextAsync(FakeSessionHost.IsStart, ct);
        var session = (await Runtime(brain).Inspect(ct)).SessionId!;
        time.Now += TimeSpan.FromMinutes(41);
        await Edge(brain).Heartbeat(session);
        await Runtime(brain).AsReference<IRemindable>().ReceiveReminder(BrainRuntimeNeuron.ReconcileReminder, default, ct);

        var stream = brain.Services.GetRequiredService<DigitalBrain.Microsoft.CSharp.Sessions.SessionStreams>().Open(session);
        await Runtime(brain).AsReference<IRemindable>().ReceiveReminder(BrainRuntimeNeuron.ReconcileReminder, default, ct);

        Assert.True(stream.Reader.TryRead(out var pushed));
        Assert.Equal(DigitalBrain.Client.Runtime.V1.TokenEvent.Kind, pushed.Kind);
    }

    // A sandbox endpoint that accepts connections and never answers must not hold the runtime's turn.
    [Trait("host", "own")]
    [Fact]
    public async Task AHangingSandboxNeverHoldsTheRuntime()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeSessionHost { Hang = true };
        await using var brain = await RuntimeBrain.StartAsync(host, ct);

        await Programs(brain).Assign(Owner, Synapse(), []);
        await Runtime(brain).AsReference<IRemindable>().ReceiveReminder(BrainRuntimeNeuron.ReconcileReminder, default, ct);

        Assert.Equal(SessionStatus.Starting, (await Runtime(brain).Inspect(ct)).Session);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AssigningAProgramCompilesEverySynapseAndStartsOneSession()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeSessionHost();
        await using var brain = await RuntimeBrain.StartAsync(host, ct);

        var view = await Programs(brain).Assign("acc-brain/apps/demo",
            new() { ["synapses/a.cs"] = RuntimeBrain.Idle, ["synapses/b.cs"] = RuntimeBrain.Idle, ["lib/Shared.cs"] = "public static class Shared {}\n" },
            new() { ["App"] = "acc-brain/apps/demo" });

        Assert.Equal(["acc-brain/apps/demo/synapses/a.cs", "acc-brain/apps/demo/synapses/b.cs"], view.Behaviors.Select(b => b.Id));
        Assert.All(view.Behaviors, behavior => Assert.Equal("Starting", behavior.Status));
        await host.NextAsync(FakeSessionHost.IsStart, ct);
        var session = (await Runtime(brain).Inspect(ct)).SessionId!;
        var assignments = await Edge(brain).Assignments(session);
        Assert.Equal(2, assignments.Count);
        Assert.All(assignments, assignment => Assert.Equal("acc-brain/apps/demo", assignment.Settings["App"]));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AProgramThatDoesNotCompileIsRefusedAndTheRunningOneIsUntouched()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeSessionHost();
        await using var brain = await RuntimeBrain.StartAsync(host, ct);
        var before = await Programs(brain).Assign("acc-brain/apps/demo", new() { ["synapses/a.cs"] = RuntimeBrain.Idle }, []);
        await host.NextAsync(FakeSessionHost.IsStart, ct);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Programs(brain).Assign("acc-brain/apps/demo",
            new() { ["synapses/a.cs"] = RuntimeBrain.Idle, ["synapses/b.cs"] = "int x = \"no\";\n" }, []));

        Assert.Contains("synapses/b.cs", error.Message, StringComparison.Ordinal);
        Assert.Contains("CS0029", error.Message, StringComparison.Ordinal);
        Assert.Equal(before.Hash, (await Programs(brain).Describe("acc-brain/apps/demo"))!.Hash);
        Assert.Single((await Programs(brain).Describe("acc-brain/apps/demo"))!.Behaviors);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ReassigningWithoutASynapseRetiresOnlyThatBehaviorAndKeepsTheOthersSubscriptions()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeSessionHost();
        await using var brain = await RuntimeBrain.StartAsync(host, ct);
        await Programs(brain).Assign("acc-brain/apps/demo", new() { ["synapses/a.cs"] = RuntimeBrain.Idle, ["synapses/b.cs"] = RuntimeBrain.Idle }, []);
        var session = (await Runtime(brain).Inspect(ct)).SessionId!;
        var source = brain.Get<IPinger>("one2");
        var kept = await Edge(brain).Subscribed(session, "acc-brain/apps/demo/synapses/a.cs", source.GetGrainId().ToString(), nameof(Pinged));
        await Edge(brain).Subscribed(session, "acc-brain/apps/demo/synapses/b.cs", source.GetGrainId().ToString(), nameof(Pinged));
        await using var changes = await brain.Observe<BrainRuntimeChanged>(Runtime(brain), ct);
        await source.Ping(1); await changes.NextAsync(ct: ct);

        var view = await Programs(brain).Assign("acc-brain/apps/demo", new() { ["synapses/a.cs"] = RuntimeBrain.Idle }, []);

        Assert.Equal(["acc-brain/apps/demo/synapses/a.cs"], view.Behaviors.Select(b => b.Id));
        var pending = Assert.Single(await Edge(brain).PendingBatches(session));
        Assert.Equal(kept, pending.Subscription);
        Assert.Single(pending.Signals);
    }

    // A synapse whose code changed is a new reaction: what the old assembly subscribed to is dropped, and the
    // new one declares its own. Otherwise a source the new code no longer reads fills a buffer nobody acknowledges.
    [Trait("host", "own")]
    [Fact]
    public async Task AChangedSynapseDropsTheOldAssemblysSubscriptions()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        var before = await Programs(brain).Assign(Owner, Synapse(), []);
        var session = (await Runtime(brain).Inspect(ct)).SessionId!;
        var pinger = brain.Get<IPinger>("changed");
        await Edge(brain).Subscribed(session, Behavior, pinger.GetGrainId().ToString(), nameof(Pinged));
        await using var changes = await brain.Observe<BrainRuntimeChanged>(Runtime(brain), ct);
        await pinger.Ping(1); await changes.NextAsync(ct: ct);

        var after = await Programs(brain).Assign(Owner, Synapse(RuntimeBrain.Idle + "// v2\n"), []);

        Assert.NotEqual(before.Behaviors.Single().Hash, after.Behaviors.Single().Hash);
        Assert.Empty(after.Behaviors.Single().Subscriptions);
        Assert.Empty(await Edge(brain).PendingBatches(session));
    }

    // Compiling and storing a program takes seconds of CPU and I/O, both before the program takes the runtime's
    // turn; the runtime's other calls go on meanwhile: the edge resolving a script's identity, the shell
    // inspecting, the host's heartbeat. The fact holds the store, the step after the compile.
    [Trait("host", "own")]
    [Fact]
    public async Task AProgramStillBeingStoredDoesNotHoldTheRestOfTheRuntime()
    {
        var ct = TestContext.Current.CancellationToken;
        var assemblies = new HeldAssemblies();
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct, assemblies: assemblies);
        await Programs(brain).Assign(Owner, Synapse(), []);
        var session = (await Runtime(brain).Inspect(ct)).SessionId!;
        assemblies.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var assigning = Programs(brain).Assign("acc-brain/apps/other", Synapse(), []);
        await assemblies.Entered.Task.WaitAsync(ct);

        try
        {
            var within = brain.Execution.AssertionTimeout;
            Assert.NotNull(await Edge(brain).Identity(session, Behavior).WaitAsync(within, ct));
            Assert.Single((await Runtime(brain).Inspect(ct).WaitAsync(within, ct)).Behaviors);
            await Edge(brain).Heartbeat(session).WaitAsync(within, ct);
        }
        finally { assemblies.Hold.TrySetResult(); }

        Assert.Single((await assigning).Behaviors);
        Assert.Equal(2, (await Runtime(brain).Inspect(ct)).Behaviors.Count);
    }

    // Two programs of one owner compile side by side; the one assigned last wins even when the older one finishes
    // compiling after it, and the older one says it was not applied.
    [Trait("host", "own")]
    [Fact]
    public async Task AnAssignOvertakenWhileItCompiledIsNotApplied()
    {
        var ct = TestContext.Current.CancellationToken;
        var assemblies = new HeldAssemblies();
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct, assemblies: assemblies);
        var hold = assemblies.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var older = Programs(brain).Assign(Owner, Synapse(RuntimeBrain.Idle + "// older\n"), []);
        await assemblies.Entered.Task.WaitAsync(ct);
        assemblies.Hold = null;

        var newer = await Programs(brain).Assign(Owner, Synapse(RuntimeBrain.Idle + "// newer\n"), []);
        hold.SetResult();

        await Assert.ThrowsAsync<InvalidOperationException>(() => older);
        Assert.Equal(newer.Hash, (await Programs(brain).Describe(Owner))!.Hash);
    }

    // A caller whose Assign timed out asks again: the same program joins the one compiling, and once applied
    // answers it, so it is compiled and stored once.
    [Trait("host", "own")]
    [Fact]
    public async Task TheSameProgramAssignedAgainJoinsTheAssignInFlightAndIsStoredOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var assemblies = new HeldAssemblies();
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct, assemblies: assemblies);
        var hold = assemblies.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Programs(brain).Assign(Owner, Synapse(), []);
        await assemblies.Entered.Task.WaitAsync(ct);

        var again = Programs(brain).Assign(Owner, Synapse(), []);
        hold.SetResult();

        Assert.Equal((await first).Hash, (await again).Hash);
        Assert.Equal((await first).Hash, (await Programs(brain).Assign(Owner, Synapse(), [])).Hash);
        Assert.Equal(1, assemblies.Puts);
    }

    // The fast path's revert: a write whose Assign never answered (asked twice, joined) is given up and the app
    // asks for its previous program again. That program is current, so it answers at once, and the stuck Assign
    // that lands afterwards is refused by ticket: the runtime keeps the previous program.
    [Trait("host", "own")]
    [Fact]
    public async Task AStuckAssignThatLandsAfterTheRevertToTheCurrentProgramIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var assemblies = new HeldAssemblies();
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct, assemblies: assemblies);
        var previous = await Programs(brain).Assign(Owner, Synapse(), []);
        var hold = assemblies.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var stuck = Programs(brain).Assign(Owner, Synapse(RuntimeBrain.Idle + "// stuck\n"), []);
        await assemblies.Entered.Task.WaitAsync(ct);
        var askedAgain = Programs(brain).Assign(Owner, Synapse(RuntimeBrain.Idle + "// stuck\n"), []);
        assemblies.Hold = null;

        var reverted = await Programs(brain).Assign(Owner, Synapse(), []);
        hold.SetResult();

        Assert.Equal(previous.Hash, reverted.Hash);
        await Assert.ThrowsAsync<InvalidOperationException>(() => stuck);
        await Assert.ThrowsAsync<InvalidOperationException>(() => askedAgain);
        Assert.Equal(previous.Hash, (await Programs(brain).Describe(Owner))!.Hash);
    }

    // A live signal only nudges the runtime: its publisher returns while the runtime is still in a long turn,
    // and the signal is buffered once that turn ends.
    [Trait("host", "own")]
    [Fact]
    public async Task APublisherDoesNotWaitOutTheRuntimesTurn()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        await Programs(brain).Assign(Owner, Synapse(), []);
        var session = (await Runtime(brain).Inspect(ct)).SessionId!;
        var pinger = brain.Get<IPinger>("nudging");
        var subscription = await Edge(brain).Subscribed(session, Behavior, pinger.GetGrainId().ToString(), nameof(Pinged));
        await using var changes = await brain.Observe<BrainRuntimeChanged>(Runtime(brain), ct);
        // Seeding a subscription from this source holds the runtime's turn until the source answers.
        var held = brain.Get<IHeldRetainer>("held");
        var seeding = Edge(brain).Subscribed(session, Behavior, held.GetGrainId().ToString(), nameof(Pinged));
        await HeldRetainer.Asked.Task.WaitAsync(ct);

        try { await pinger.Ping(1).WaitAsync(brain.Execution.AssertionTimeout, ct); }
        finally { HeldRetainer.Release.TrySetResult(); }

        await seeding;
        while (!(await Edge(brain).PendingBatches(session)).Any(batch => batch.Subscription == subscription)) { await changes.NextAsync(ct: ct); }
        Assert.Equal(1, SignalJson.Number(Assert.Single((await Edge(brain).PendingBatches(session)).Single(batch => batch.Subscription == subscription).Signals)));
    }

    // Stores assemblies, and holds every store once a fact arms it, so the fact sees what goes on meanwhile.
    private sealed class HeldAssemblies : DigitalBrain.Microsoft.CSharp.Assemblies.IBehaviorAssemblies
    {
        public TaskCompletionSource? Hold { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _puts;
        public int Puts => Volatile.Read(ref _puts);

        public async Task Put(DigitalBrain.Microsoft.CSharp.Catalog.CompiledBehavior behavior, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _puts);
            if (Hold is not { } hold) { return; }
            Entered.TrySetResult();
            await hold.Task.WaitAsync(cancellationToken);
        }

        public Task<bool> Exists(string hash, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<Uri> ReadUrl(string name, TimeSpan lifetime, CancellationToken cancellationToken)
            => Task.FromResult(new Uri("memory://behaviors/" + name));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task RetiringTheLastProgramStopsTheSession()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeSessionHost();
        await using var brain = await RuntimeBrain.StartAsync(host, ct);
        await Programs(brain).Assign("acc-brain/apps/demo", new() { ["synapses/a.cs"] = RuntimeBrain.Idle }, []);
        await host.NextAsync(FakeSessionHost.IsStart, ct);

        await Programs(brain).Retire("acc-brain/apps/demo");

        await host.NextAsync(FakeSessionHost.IsStop, ct);
        Assert.Null(await Programs(brain).Describe("acc-brain/apps/demo"));
        Assert.Equal(SessionStatus.None, (await Runtime(brain).Inspect(ct)).Session);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task IdentityAnswersThePrincipalAndOwnerOfABehavior()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        await Programs(brain).Assign("acc-brain/apps/demo", new() { ["synapses/a.cs"] = RuntimeBrain.Idle }, []);
        var session = (await Runtime(brain).Inspect(ct)).SessionId!;

        var identity = await Edge(brain).Identity(session, "acc-brain/apps/demo/synapses/a.cs");

        Assert.NotNull(identity);
        Assert.Equal("acc-brain/apps/demo", identity!.Owner);
        Assert.Null(await Edge(brain).Identity(session, "acc-brain/apps/other/synapses/a.cs"));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AReportedExitIsRememberedForDescribeAndNeverDisarms()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        var view = await Programs(brain).Assign("acc-brain/apps/demo", new() { ["synapses/a.cs"] = RuntimeBrain.Idle }, []);
        var session = (await Runtime(brain).Inspect(ct)).SessionId!;
        var hash = view.Behaviors[0].Hash;

        for (var attempt = 0; attempt < 10; attempt++)
        { await Edge(brain).Reported(session, new("acc-brain/apps/demo/synapses/a.cs", hash, "Exited", 1, "boom")); }

        var described = Assert.Single((await Programs(brain).Describe("acc-brain/apps/demo"))!.Behaviors);
        Assert.Equal("Exited", described.Status);
        Assert.Equal("boom", described.Error);
        Assert.Single(await Edge(brain).Assignments(session));   // still assigned, never disarmed
    }

    [Fact]
    public void AHostThatReportsMoreAssembliesStillCoversTheSilosContracts()
    {
        var silo = ContractsFingerprint.Of([typeof(Signal).Assembly]);
        Assert.True(ContractsFingerprint.Covers(ContractsFingerprint.Of([typeof(Signal).Assembly, typeof(ContractsFingerprint).Assembly]), silo));
        Assert.False(ContractsFingerprint.Covers(ContractsFingerprint.Of([typeof(ContractsFingerprint).Assembly]), silo));
    }

    private static IBrainPrograms Programs(ITestBrain brain) => RuntimeBrain.Programs(brain, Brain);

    private static IBrainRuntime Runtime(ITestBrain brain) => RuntimeBrain.Runtime(brain, Brain);

    private static IBrainRuntimeEdge Edge(ITestBrain brain) => RuntimeBrain.Edge(brain, Brain);
}

// A retaining source whose backlog read holds until released: the runtime seeding a subscription from it
// stays in that turn meanwhile.
[Alias("tests.csharp.held-retainer")]
public interface IHeldRetainer : IRetainsSignals;

public sealed class HeldRetainer : Neuron, IHeldRetainer
{
    public static TaskCompletionSource Asked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<RetainedSignal[]> Retained(string signal, long since)
    {
        Asked.TrySetResult();
        await Release.Task;
        return [];
    }
}
