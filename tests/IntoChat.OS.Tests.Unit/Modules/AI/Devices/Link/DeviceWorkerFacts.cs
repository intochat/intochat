using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DigitalBrain.Identity.Local.Machine;
using DigitalBrain.Runtime.Orleans;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Devices.Link;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace IntoChat.OS.Modules.Devices.Tests.Unit.Link;

public sealed class DeviceWorkerFacts
{
    [Fact]
    public async Task ReadyModelsAreReportedOnConnectAndWhenAnInstallBecomesReady()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Open(ready: ["qwen2.5-coder:14b"]);

        await harness.Worker.Tick(ct);
        Assert.NotNull(harness.Server.LastReport);
        Assert.Equal(["qwen2.5-coder:14b"], harness.Server.LastReport!.Models.Select(static m => m.Id));

        harness.Runtime.AddInstalled("llama3.2:3b");
        harness.Worker.NotifyInstallChanged(new InstallChanged(new ModelId("llama3.2:3b"), InstallState.Ready, 100));

        await harness.Worker.Tick(ct);
        Assert.Equal(2, harness.Server.LastReport!.Models.Length);
        Assert.Contains(harness.Server.LastReport!.Models, m => m.Id == "llama3.2:3b");
    }

    [Fact]
    public async Task AJobRunsOnTheLocalRuntimeAndStreamsNdjson()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Open(ready: ["qwen2.5-coder:14b"], runtimeAnswers: ["he", "llo"]);
        var parentTraceId = ActivityTraceId.CreateRandom();
        var parentSpanId = ActivitySpanId.CreateRandom();
        var traceParent = $"00-{parentTraceId}-{parentSpanId}-01";

        harness.Server.Enqueue(new DeviceChatJob(
            "j1",
            "qwen2.5-coder:14b",
            [new DeviceMessage("user", "hi")],
            Temperature: 0.2f,
            MaxOutputTokens: 128,
            TraceParent: traceParent));

        await harness.Worker.Tick(ct);

        var chunks = harness.Server.Stream("j1");
        Assert.Equal(["he", "llo", null], chunks.Select(static c => c.Delta));
        Assert.True(chunks.Last().Done);
        Assert.Null(chunks.Last().Error);
        Assert.Equal(parentTraceId.ToString(), harness.Runtime.LastTraceId);
    }

    [Fact]
    public async Task AJobForAModelThatIsNotReadyIsRefusedOnTheStream()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Open(ready: ["qwen2.5-coder:14b"]);
        harness.Server.Enqueue(new DeviceChatJob(
            "j-missing",
            "llama3.2:3b",
            [new DeviceMessage("user", "hi")],
            null,
            null,
            null));

        await harness.Worker.Tick(ct);

        var chunk = Assert.Single(harness.Server.Stream("j-missing"));
        Assert.Equal("not-ready", chunk.Error);
        Assert.True(chunk.Done);
        Assert.Equal(0, harness.Runtime.ChatCalls);
    }

    [Fact]
    public async Task NoPromptTextIsLogged()
    {
        var ct = TestContext.Current.CancellationToken;
        const string secretPrompt = "super-private-prompt-hi";
        await using var harness = await Open(ready: ["qwen2.5-coder:14b"], runtimeAnswers: ["hello-secret-answer"]);
        harness.Server.Enqueue(new DeviceChatJob(
            "j-log",
            "qwen2.5-coder:14b",
            [new DeviceMessage("user", secretPrompt)],
            null,
            null,
            null));

        await harness.Worker.Tick(ct);

        Assert.NotEmpty(harness.Logs);
        Assert.All(harness.Logs, line =>
        {
            Assert.DoesNotContain(secretPrompt, line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("hello-secret-answer", line, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task IdleWhenNotJoined()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Open(ready: ["qwen2.5-coder:14b"], joined: false);

        await harness.Worker.Tick(ct);

        Assert.Empty(harness.Server.RequestPaths);
    }

    [Fact]
    public async Task BlockedSandboxLongPollDoesNotStarveChatOrResourceLanes()
    {
        var ct = TestContext.Current.CancellationToken;
        var consent = new ClientCodeSandboxConsentStore(initialAllow: true);
        consent.SetAllowWindowsFolderGateway(true);
        var sandbox = new FakeLocalSandboxExecutor();
        var resource = new FakeLocalResourceExecutor();

        await using var harness = await Open(
            ready: ["qwen2.5-coder:14b"],
            runtimeAnswers: ["pong"],
            localSandbox: sandbox,
            consentStore: consent,
            resourceConsent: consent,
            localResourceExecutor: resource);

        // Simulate a 25s long-poll hanging on /circle/v1/compute/sandbox/next.
        var sandboxPollStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSandboxPoll = new TaskCompletionSource<ConnectedSandboxRequest?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sandboxBlockedOnce = 0;
        harness.Server.SandboxPollOverride = async token =>
        {
            if (Interlocked.Exchange(ref sandboxBlockedOnce, 1) == 0)
            {
                sandboxPollStarted.TrySetResult();
                return await releaseSandboxPoll.Task.WaitAsync(token);
            }

            // Subsequent polls wait until cancelled so we don't spin tightly.
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return null;
        };

        await harness.Worker.StartAsync(ct);
        try
        {
            await sandboxPollStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

            // While sandbox/next is blocked, enqueue multiple chat and resource jobs.
            harness.Server.Enqueue(NewChatJob("chat-1"));
            harness.Server.Enqueue(NewChatJob("chat-2"));
            harness.Server.EnqueueResource(NewResourceJob("res-1"));
            harness.Server.EnqueueResource(NewResourceJob("res-2"));

            await WaitUntilAsync(
                () => harness.Server.Stream("chat-1").Count > 0
                    && harness.Server.Stream("chat-2").Count > 0
                    && harness.Server.ResourceResult("res-1") is { Ok: true }
                    && harness.Server.ResourceResult("res-2") is { Ok: true },
                TimeSpan.FromSeconds(5),
                ct);

            // Sandbox poll is still blocked — chat and resource finished with zero starvation.
            Assert.False(releaseSandboxPoll.Task.IsCompleted);
            Assert.Equal(["pong", null], harness.Server.Stream("chat-1").Select(static c => c.Delta));
            Assert.Equal(["pong", null], harness.Server.Stream("chat-2").Select(static c => c.Delta));
            Assert.Equal(harness.MachineId, harness.Server.ResourceResult("res-1")!.ExecutedByMachineId);
            Assert.Equal(harness.MachineId, harness.Server.ResourceResult("res-2")!.ExecutedByMachineId);

            // Release the blocked sandbox poll with a real job and confirm it executes too.
            releaseSandboxPoll.TrySetResult(NewSandboxJob("sb-1"));
            await WaitUntilAsync(
                () => harness.Server.SandboxResult("sb-1") is { Handled: true },
                TimeSpan.FromSeconds(5),
                ct);

            Assert.Equal(harness.MachineId, harness.Server.SandboxResult("sb-1")!.ExecutedByMachineId);
        }
        finally
        {
            await harness.Worker.StopAsync(ct);
        }
    }

    [Fact]
    public async Task BlockedChatLongPollDoesNotStarveSandboxResourceOrHeartbeats()
    {
        var ct = TestContext.Current.CancellationToken;
        var consent = new ClientCodeSandboxConsentStore(initialAllow: true);
        consent.SetAllowWindowsFolderGateway(true);
        var sandbox = new FakeLocalSandboxExecutor();
        var resource = new FakeLocalResourceExecutor();
        var options = new StaticOptionsMonitor<DevicesOptions>(new DevicesOptions
        {
            Compute = new ComputeCapabilityOptions
            {
                ConsentMode = ComputeConsentMode.ExplicitToggle,
                AllowClientCodeSandbox = true,
                HeartbeatIntervalSeconds = 15,
            },
        });

        await using var harness = await Open(
            ready: ["qwen2.5-coder:14b"],
            options: options,
            localSandbox: sandbox,
            consentStore: consent,
            resourceConsent: consent,
            localResourceExecutor: resource);

        var chatPollStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var chatPollCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Server.ChatPollOverride = async token =>
        {
            chatPollStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return null;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                chatPollCancelled.TrySetResult();
                throw;
            }
        };

        await harness.Worker.StartAsync(ct);
        try
        {
            await chatPollStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Equal(1, harness.Server.CapabilityReportsCount);

            harness.Server.EnqueueSandbox(NewSandboxJob("sb-concurrent"));
            harness.Server.EnqueueResource(NewResourceJob("res-concurrent"));

            await WaitUntilAsync(
                () => harness.Server.SandboxResult("sb-concurrent") is { Handled: true }
                    && harness.Server.ResourceResult("res-concurrent") is { Ok: true },
                TimeSpan.FromSeconds(5),
                ct);

            // Advance FakeTimeProvider by the heartbeat interval while chat is STILL blocked on long-poll.
            harness.Clock.Advance(TimeSpan.FromSeconds(15));
            await WaitUntilAsync(
                () => harness.Server.CapabilityReportsCount >= 2,
                TimeSpan.FromSeconds(5),
                ct);

            Assert.False(chatPollCancelled.Task.IsCompleted);
        }
        finally
        {
            await harness.Worker.StopAsync(ct);
        }

        // Stopping the worker cleanly cancels the in-flight blocked chat long-poll.
        await chatPollCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
    }

    [Fact]
    public async Task PerLaneErrorIsolationAndBoundedJitteredExponentialBackoff()
    {
        var ct = TestContext.Current.CancellationToken;
        var consent = new ClientCodeSandboxConsentStore(initialAllow: true);
        consent.SetAllowWindowsFolderGateway(true);
        var sandbox = new FakeLocalSandboxExecutor();
        var resource = new FakeLocalResourceExecutor();

        // Deterministic jitter=0.5 => jitter factor = 0.75 + 0.5*0.5 = 1.0 (exact 500ms, 1000ms, 2000ms).
        await using var harness = await Open(
            ready: ["qwen2.5-coder:14b"],
            runtimeAnswers: ["still-alive"],
            localSandbox: sandbox,
            consentStore: consent,
            resourceConsent: consent,
            localResourceExecutor: resource,
            jitterProvider: static () => 0.5);

        var sandboxAttempts = 0;
        var thirdFailureObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Server.SandboxPollOverride = async token =>
        {
            var attempt = Interlocked.Increment(ref sandboxAttempts);
            if (attempt <= 3)
            {
                if (attempt == 3)
                {
                    thirdFailureObserved.TrySetResult();
                }

                throw new HttpRequestException($"Simulated sandbox relay 500 (attempt {attempt}).");
            }

            if (attempt == 4)
            {
                return NewSandboxJob("sb-recovered");
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return null;
        };

        await harness.Worker.StartAsync(ct);
        try
        {
        // Wait for attempt 1 to fail and register its 500ms backoff timer.
            await harness.Clock.WaitForTimerAsync(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5), ct);

            // While sandbox is failing/backing off, chat and resource lanes keep executing without delay.
            harness.Server.Enqueue(NewChatJob("chat-during-sandbox-error"));
            harness.Server.EnqueueResource(NewResourceJob("res-during-sandbox-error"));

            await WaitUntilAsync(
                () => harness.Server.Stream("chat-during-sandbox-error").Count > 0
                    && harness.Server.ResourceResult("res-during-sandbox-error") is { Ok: true },
                TimeSpan.FromSeconds(5),
                ct);

            // Sandbox should still be waiting in its 500ms backoff (not spinning).
            Assert.Equal(1, Volatile.Read(ref sandboxAttempts));

            // Advance 500ms -> attempt 2 fails and registers its 1000ms backoff timer.
            harness.Clock.Advance(TimeSpan.FromMilliseconds(500));
            await harness.Clock.WaitForTimerAsync(TimeSpan.FromMilliseconds(1000), TimeSpan.FromSeconds(5), ct);
            Assert.Equal(2, Volatile.Read(ref sandboxAttempts));

            // Advance 900ms (not yet 1000ms) -> still waiting.
            harness.Clock.Advance(TimeSpan.FromMilliseconds(900));
            await Task.Delay(25, ct);
            Assert.Equal(2, Volatile.Read(ref sandboxAttempts));

            // Advance remaining 100ms -> attempt 3 fails and registers its 2000ms backoff timer.
            harness.Clock.Advance(TimeSpan.FromMilliseconds(100));
            await thirdFailureObserved.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            await harness.Clock.WaitForTimerAsync(TimeSpan.FromMilliseconds(2000), TimeSpan.FromSeconds(5), ct);

            // Advance 2000ms -> attempt 4 succeeds and executes the sandbox job.
            harness.Clock.Advance(TimeSpan.FromMilliseconds(2000));
            await WaitUntilAsync(
                () => harness.Server.SandboxResult("sb-recovered") is { Handled: true },
                TimeSpan.FromSeconds(5),
                ct);
        }
        finally
        {
            await harness.Worker.StopAsync(ct);
        }

        // Verify ComputeBackoffDelay bounds and capping directly.
        Assert.Equal(TimeSpan.FromMilliseconds(375), DeviceWorker.ComputeBackoffDelay(1, static () => 0.0));
        Assert.Equal(TimeSpan.FromMilliseconds(500), DeviceWorker.ComputeBackoffDelay(1, static () => 0.5));
        Assert.Equal(TimeSpan.FromMilliseconds(625), DeviceWorker.ComputeBackoffDelay(1, static () => 1.0));
        Assert.Equal(TimeSpan.FromMilliseconds(1000), DeviceWorker.ComputeBackoffDelay(2, static () => 0.5));
        Assert.Equal(TimeSpan.FromMilliseconds(2000), DeviceWorker.ComputeBackoffDelay(3, static () => 0.5));
        Assert.Equal(DeviceWorker.MaxBackoffDelay, DeviceWorker.ComputeBackoffDelay(10, static () => 1.0));
        Assert.True(DeviceWorker.ComputeBackoffDelay(10, static () => 0.0) <= DeviceWorker.MaxBackoffDelay);
    }

    [Fact]
    public async Task RevocationOnOneLaneStopsAllBlockedLanesAndRevokesCredentials()
    {
        var ct = TestContext.Current.CancellationToken;
        var consent = new ClientCodeSandboxConsentStore(initialAllow: true);
        consent.SetAllowWindowsFolderGateway(true);
        var sandbox = new FakeLocalSandboxExecutor();
        var resource = new FakeLocalResourceExecutor();

        await using var harness = await Open(
            ready: ["qwen2.5-coder:14b"],
            localSandbox: sandbox,
            consentStore: consent,
            resourceConsent: consent,
            localResourceExecutor: resource);

        var sandboxBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sandboxCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resourceBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resourceCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        harness.Server.SandboxPollOverride = async token =>
        {
            sandboxBlocked.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return null;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                sandboxCancelled.TrySetResult();
                throw;
            }
        };

        harness.Server.ResourcePollOverride = async token =>
        {
            resourceBlocked.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return null;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                resourceCancelled.TrySetResult();
                throw;
            }
        };

        var triggerRevoke = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Server.ChatPollOverride = async token =>
        {
            await triggerRevoke.Task.WaitAsync(token);
            return null;
        };

        await harness.Worker.StartAsync(ct);
        try
        {
            await Task.WhenAll(
                sandboxBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5), ct),
                resourceBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5), ct));

            // Now reject authenticated calls with 401 and release chat poll to observe revocation.
            harness.Server.RejectWith401 = true;
            triggerRevoke.TrySetResult();

            await Task.WhenAll(
                sandboxCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), ct),
                resourceCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), ct));

            await WaitUntilAsync(() => harness.Credentials.IsRevoked, TimeSpan.FromSeconds(5), ct);
            Assert.False(File.Exists(harness.Credentials.FilePath));
        }
        finally
        {
            await harness.Worker.StopAsync(ct);
        }
    }

    [Fact]
    public async Task StreamingSandboxExecutorStreamsNdjsonChunksAndFallsBackOn404()
    {
        var ct = TestContext.Current.CancellationToken;
        var consent = new ClientCodeSandboxConsentStore(initialAllow: true);
        var streamingExecutor = new FakeStreamingSandboxExecutor(
            chunks:
            [
                new SandboxStreamChunk(Progress: "compiling"),
                new SandboxStreamChunk(Stdout: "hello stdout\n"),
                new SandboxStreamChunk(Stderr: "warn stderr\n"),
                new SandboxStreamChunk(
                    Done: true,
                    Result: new ConnectedSandboxResponse(
                        Handled: true,
                        ResultJson: "{\"ok\":true}",
                        UpdatedStateJson: null,
                        Error: null)),
            ]);

        await using var harness = await Open(
            ready: ["qwen2.5-coder:14b"],
            localSandbox: streamingExecutor,
            consentStore: consent);

        // 1. Relay supports /circle/v1/compute/sandbox/{jobId}/stream -> streams all NDJSON chunks and stamps ExecutedByMachineId.
        harness.Server.EnqueueSandbox(NewSandboxJob("sb-stream-ok"));
        await harness.Worker.Tick(ct);

        var streamed = harness.Server.SandboxStream("sb-stream-ok");
        Assert.Equal(4, streamed.Count);
        Assert.Equal("compiling", streamed[0].Progress);
        Assert.Equal("hello stdout\n", streamed[1].Stdout);
        Assert.Equal("warn stderr\n", streamed[2].Stderr);
        Assert.True(streamed[3].Done);
        Assert.NotNull(streamed[3].Result);
        Assert.True(streamed[3].Result!.Handled);
        Assert.Equal("{\"ok\":true}", streamed[3].Result!.ResultJson);
        Assert.Equal(harness.MachineId, streamed[3].Result!.ExecutedByMachineId);
        Assert.Null(harness.Server.SandboxResult("sb-stream-ok"));

        // 2. Mid-stream exception still emits terminal Done=true error chunk on the NDJSON stream.
        streamingExecutor.ThrowAfterFirstChunk = new InvalidOperationException("sandbox crashed");
        harness.Server.EnqueueSandbox(NewSandboxJob("sb-stream-err"));
        await harness.Worker.Tick(ct);

        var errStream = harness.Server.SandboxStream("sb-stream-err");
        Assert.Equal(2, errStream.Count);
        Assert.Equal("compiling", errStream[0].Progress);
        Assert.True(errStream[1].Done);
        Assert.Equal("sandbox crashed", errStream[1].Error);
        Assert.NotNull(errStream[1].Result);
        Assert.False(errStream[1].Result!.Handled);
        Assert.Equal(harness.MachineId, errStream[1].Result!.ExecutedByMachineId);

        // 3. Older relay returning 404 on /stream seamlessly falls back to POST /sandbox/{jobId}/result.
        streamingExecutor.ThrowAfterFirstChunk = null;
        harness.Server.Return404ForSandboxStream = true;
        harness.Server.EnqueueSandbox(NewSandboxJob("sb-stream-fallback"));
        await harness.Worker.Tick(ct);

        var fallbackResult = harness.Server.SandboxResult("sb-stream-fallback");
        Assert.NotNull(fallbackResult);
        Assert.True(fallbackResult!.Handled);
        Assert.Equal(harness.MachineId, fallbackResult.ExecutedByMachineId);
    }

    private static DeviceChatJob NewChatJob(string jobId) => new(
        jobId,
        "qwen2.5-coder:14b",
        [new DeviceMessage("user", "ping")],
        Temperature: null,
        MaxOutputTokens: 32,
        TraceParent: null);

    private static ConnectedSandboxRequest NewSandboxJob(string jobId) => new(
        JobId: jobId,
        BrainId: "owner",
        AppKey: "test-app",
        Version: "1.0.0",
        Digest: "sha256:test",
        AssemblyBase64: string.Empty,
        Command: "run",
        PayloadJson: "{}",
        StateJson: null,
        SettingsJson: null,
        KernelCallbackUrl: null,
        AppToken: "tok",
        AllowedContracts: []);

    private static PcResourceRequest NewResourceJob(string jobId) => new(
        JobId: jobId,
        BrainId: "owner",
        Capability: ClientComputeConsentPolicy.WindowsFolderGatewayCapability,
        Operation: "list",
        PayloadJson: "{}");

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException($"Condition was not satisfied within {timeout.TotalSeconds:F1}s.");
            }

            await Task.Delay(15, ct);
        }
    }

    private static async Task<Harness> Open(
        string[] ready,
        string[]? runtimeAnswers = null,
        bool joined = true,
        IOptionsMonitor<DevicesOptions>? options = null,
        ILocalSandboxExecutor? localSandbox = null,
        IClientCodeSandboxConsent? consentStore = null,
        IClientResourceConsent? resourceConsent = null,
        ILocalPcResourceExecutor? localResourceExecutor = null,
        Func<double>? jitterProvider = null)
    {
        var tempRoot = new DigitalBrain.Testing.TempDirectory("intochat-device-worker-facts-");
        var root = tempRoot.Path;
        var clock = new TrackingFakeTimeProvider(new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero));
        var machine = new MachineId(Path.Combine(root, "machine-id"));
        var credentials = new DeviceCredentials(new XorProtector(), Path.Combine(root, "devices", "link.bin"));
        if (joined)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            await credentials.Write(new LinkSecrets(
                new Uri("https://cloud.test"),
                "cred1",
                "cred1.access-1",
                clock.GetUtcNow().AddMinutes(15),
                "cred1.refresh-1",
                clock.GetUtcNow().AddDays(30),
                key.ExportPkcs8PrivateKey()));
        }

        var server = new FakeRelayServer();
        var http = new HttpClient(server);
        var cloud = new CloudClient(http, credentials, clock);
        var runtime = new FakeLocalRuntime(ready, runtimeAnswers ?? ["ok"]);
        var logs = new List<string>();
        var logger = new ListLogger<DeviceWorker>(logs);
        var worker = new DeviceWorker(
            cloud,
            credentials,
            [runtime],
            machine,
            logger,
            clock,
            () => "vlad-pc",
            services: null,
            options: options,
            computeRouter: null,
            localSandbox: localSandbox,
            consentStore: consentStore,
            resourceConsent: resourceConsent,
            localResourceExecutor: localResourceExecutor,
            jitterProvider: jitterProvider);

        return new Harness(worker, server, runtime, logs, http, tempRoot, clock, credentials, machine.Value);
    }

    private sealed class Harness(
        DeviceWorker worker,
        FakeRelayServer server,
        FakeLocalRuntime runtime,
        List<string> logs,
        HttpClient http,
        DigitalBrain.Testing.TempDirectory tempRoot,
        TrackingFakeTimeProvider clock,
        DeviceCredentials credentials,
        string machineId) : IAsyncDisposable
    {
        public DeviceWorker Worker => worker;
        public FakeRelayServer Server => server;
        public FakeLocalRuntime Runtime => runtime;
        public IReadOnlyList<string> Logs => logs;
        public TrackingFakeTimeProvider Clock => clock;
        public DeviceCredentials Credentials => credentials;
        public string MachineId => machineId;

        public ValueTask DisposeAsync()
        {
            try
            {
                worker.Dispose();
                http.Dispose();
            }
            finally
            {
                tempRoot.Dispose();
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class XorProtector : IMachineKeyProtector
    {
        public byte[] Protect(byte[] key) => [.. key.Select(static b => (byte)(b ^ 0x33))];
        public byte[] Unprotect(byte[] stored) => [.. stored.Select(static b => (byte)(b ^ 0x33))];
    }

    private sealed class TrackingFakeTimeProvider(DateTimeOffset startDateTime) : FakeTimeProvider(startDateTime)
    {
        private readonly ConcurrentQueue<TimeSpan> _registeredDueTimes = new();
        private readonly SemaphoreSlim _timerCreated = new(0);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            _registeredDueTimes.Enqueue(dueTime);
            _timerCreated.Release();
            return timer;
        }

        public async Task WaitForTimerAsync(TimeSpan expectedDueTime, TimeSpan timeout, CancellationToken ct)
        {
            var deadline = DateTimeOffset.UtcNow + timeout;
            while (true)
            {
                while (_registeredDueTimes.TryDequeue(out var due))
                {
                    if (due == expectedDueTime)
                    {
                        return;
                    }
                }

                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new TimeoutException($"Timer with dueTime {expectedDueTime} was not registered within {timeout.TotalSeconds:F1}s.");
                }

                await _timerCreated.WaitAsync(remaining, ct);
            }
        }
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue => currentValue;
        public T Get(string? name) => currentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class FakeLocalSandboxExecutor : ILocalSandboxExecutor
    {
        public Task<ConnectedSandboxResponse> ExecuteAsync(ConnectedSandboxRequest request, CancellationToken ct = default)
            => Task.FromResult(new ConnectedSandboxResponse(
                Handled: true,
                ResultJson: "{\"job\":\"" + request.JobId + "\"}",
                UpdatedStateJson: null,
                Error: null));
    }

    private sealed class FakeStreamingSandboxExecutor(SandboxStreamChunk[] chunks) : IStreamingLocalSandboxExecutor
    {
        public Exception? ThrowAfterFirstChunk { get; set; }

        public Task<ConnectedSandboxResponse> ExecuteAsync(ConnectedSandboxRequest request, CancellationToken ct = default)
            => Task.FromResult(new ConnectedSandboxResponse(
                Handled: true,
                ResultJson: "{\"fallback\":true}",
                UpdatedStateJson: null,
                Error: null));

        public async IAsyncEnumerable<SandboxStreamChunk> ExecuteStreamingAsync(
            ConnectedSandboxRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            for (var i = 0; i < chunks.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return chunks[i];
                if (i == 0 && ThrowAfterFirstChunk is not null)
                {
                    throw ThrowAfterFirstChunk;
                }
            }
        }
    }

    private sealed class FakeLocalResourceExecutor : ILocalPcResourceExecutor
    {
        public Task<string[]> GetConsentedFolderLabelsAsync(CancellationToken ct = default)
            => Task.FromResult<string[]>(["Projects"]);

        public Task<PcResourceResponse> ExecuteAsync(PcResourceRequest request, CancellationToken ct = default)
            => Task.FromResult(new PcResourceResponse(Ok: true, ResultJson: "{\"job\":\"" + request.JobId + "\"}"));
    }

    private sealed class FakeRelayServer : HttpMessageHandler
    {
        private readonly ConcurrentQueue<DeviceChatJob> _jobs = new();
        private readonly ConcurrentQueue<ConnectedSandboxRequest> _sandboxJobs = new();
        private readonly ConcurrentQueue<PcResourceRequest> _resourceJobs = new();
        private readonly ConcurrentDictionary<string, List<DeviceChunk>> _streams = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, List<SandboxStreamChunk>> _sandboxStreams = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, ConnectedSandboxResponse> _sandboxResults = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, PcResourceResponse> _resourceResults = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<string> _requestPaths = new();
        private int _capabilityReportsCount;

        public IReadOnlyCollection<string> RequestPaths => _requestPaths;
        public ModelsReport? LastReport { get; private set; }
        public int CapabilityReportsCount => Volatile.Read(ref _capabilityReportsCount);
        public volatile bool RejectWith401;
        public volatile bool Return404ForSandboxStream;

        public Func<CancellationToken, Task<DeviceChatJob?>>? ChatPollOverride { get; set; }
        public Func<CancellationToken, Task<ConnectedSandboxRequest?>>? SandboxPollOverride { get; set; }
        public Func<CancellationToken, Task<PcResourceRequest?>>? ResourcePollOverride { get; set; }

        public void Enqueue(DeviceChatJob job) => _jobs.Enqueue(job);
        public void EnqueueSandbox(ConnectedSandboxRequest job) => _sandboxJobs.Enqueue(job);
        public void EnqueueResource(PcResourceRequest job) => _resourceJobs.Enqueue(job);

        public IReadOnlyList<DeviceChunk> Stream(string jobId)
            => _streams.TryGetValue(jobId, out var chunks) ? chunks : [];

        public IReadOnlyList<SandboxStreamChunk> SandboxStream(string jobId)
            => _sandboxStreams.TryGetValue(jobId, out var chunks) ? chunks : [];

        public ConnectedSandboxResponse? SandboxResult(string jobId)
            => _sandboxResults.TryGetValue(jobId, out var result) ? result : null;

        public PcResourceResponse? ResourceResult(string jobId)
            => _resourceResults.TryGetValue(jobId, out var result) ? result : null;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            _requestPaths.Enqueue(path);

            if (RejectWith401)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            if (path == DeviceRoutes.Models && request.Method == HttpMethod.Put)
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                LastReport = JsonSerializer.Deserialize(body, DeviceWireJson.Default.ModelsReport);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (path == DeviceRoutes.ComputeCapability && request.Method == HttpMethod.Put)
            {
                _ = await request.Content!.ReadAsStringAsync(cancellationToken);
                Interlocked.Increment(ref _capabilityReportsCount);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (path == DeviceRoutes.Next && request.Method == HttpMethod.Get)
            {
                var job = ChatPollOverride is not null
                    ? await ChatPollOverride(cancellationToken)
                    : (_jobs.TryDequeue(out var queued) ? queued : null);
                if (job is null)
                {
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                }

                var json = JsonSerializer.Serialize(job, DeviceWireJson.Default.DeviceChatJob);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
                };
            }

            if (path == DeviceRoutes.SandboxNext && request.Method == HttpMethod.Get)
            {
                var job = SandboxPollOverride is not null
                    ? await SandboxPollOverride(cancellationToken)
                    : (_sandboxJobs.TryDequeue(out var queued) ? queued : null);
                if (job is null)
                {
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                }

                var json = JsonSerializer.Serialize(job, DeviceWireJson.Default.ConnectedSandboxRequest);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
                };
            }

            if (path == DeviceRoutes.ResourceNext && request.Method == HttpMethod.Get)
            {
                var job = ResourcePollOverride is not null
                    ? await ResourcePollOverride(cancellationToken)
                    : (_resourceJobs.TryDequeue(out var queued) ? queued : null);
                if (job is null)
                {
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                }

                var json = JsonSerializer.Serialize(job, DeviceWireJson.Default.PcResourceRequest);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
                };
            }

            if (path.StartsWith(CircleRoutes.Prefix + "/compute/sandbox/", StringComparison.Ordinal)
                && path.EndsWith("/stream", StringComparison.Ordinal)
                && request.Method == HttpMethod.Post)
            {
                if (Return404ForSandboxStream)
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                var prefix = CircleRoutes.Prefix + "/compute/sandbox/";
                var jobId = Uri.UnescapeDataString(path[prefix.Length..^"/stream".Length]);
                var ndjson = await request.Content!.ReadAsStringAsync(cancellationToken);
                var list = new List<SandboxStreamChunk>();
                foreach (var line in ndjson.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    list.Add(JsonSerializer.Deserialize(line, DeviceWireJson.Default.SandboxStreamChunk)!);
                }
                _sandboxStreams[jobId] = list;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (path.StartsWith(CircleRoutes.Prefix + "/compute/sandbox/", StringComparison.Ordinal)
                && path.EndsWith("/result", StringComparison.Ordinal)
                && request.Method == HttpMethod.Post)
            {
                var prefix = CircleRoutes.Prefix + "/compute/sandbox/";
                var jobId = Uri.UnescapeDataString(path[prefix.Length..^"/result".Length]);
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                _sandboxResults[jobId] = JsonSerializer.Deserialize(body, DeviceWireJson.Default.ConnectedSandboxResponse)!;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (path.StartsWith(CircleRoutes.Prefix + "/compute/resource/", StringComparison.Ordinal)
                && path.EndsWith("/result", StringComparison.Ordinal)
                && request.Method == HttpMethod.Post)
            {
                var prefix = CircleRoutes.Prefix + "/compute/resource/";
                var jobId = Uri.UnescapeDataString(path[prefix.Length..^"/result".Length]);
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                _resourceResults[jobId] = JsonSerializer.Deserialize(body, DeviceWireJson.Default.PcResourceResponse)!;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (path.StartsWith(CircleRoutes.Prefix + "/compute/", StringComparison.Ordinal)
                && path.EndsWith("/stream", StringComparison.Ordinal)
                && request.Method == HttpMethod.Post)
            {
                var prefix = CircleRoutes.Prefix + "/compute/";
                var jobId = Uri.UnescapeDataString(path[prefix.Length..^"/stream".Length]);
                var ndjson = await request.Content!.ReadAsStringAsync(cancellationToken);
                var list = new List<DeviceChunk>();
                foreach (var line in ndjson.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    list.Add(JsonSerializer.Deserialize(line, DeviceWireJson.Default.DeviceChunk)!);
                }
                _streams[jobId] = list;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private sealed class FakeLocalRuntime : ILocalRuntime
    {
        private readonly List<ModelCard> _installed = [];
        private readonly string[] _answers;
        private int _chatCalls;

        public FakeLocalRuntime(string[] ready, string[] answers)
        {
            _answers = answers;
            foreach (var id in ready)
            {
                AddInstalled(id);
            }
        }

        public RuntimeId Id => RuntimeId.Ollama;
        public int ChatCalls => Volatile.Read(ref _chatCalls);
        public string? LastTraceId { get; private set; }

        public void AddInstalled(string id)
            => _installed.Add(new ModelCard(new ModelId(id), AiProvider.Ollama, id.Split(':')[0], id, Where.Device, Abilities.Text, Tier.Balanced));

        public Task<RuntimeStatus> Probe(CancellationToken ct)
            => Task.FromResult(new RuntimeStatus(Id, Reachable: true, Version: "0.9.0", Endpoint: new Uri("http://127.0.0.1:11434")));

        public async IAsyncEnumerable<ModelCard> Catalog([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            foreach (var card in _installed) { yield return card; }
        }

        public async IAsyncEnumerable<ModelCard> Installed([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            foreach (var card in _installed) { yield return card; }
        }

        public async IAsyncEnumerable<InstallProgress> Install(ModelId id, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task Remove(ModelId id, CancellationToken ct) => Task.CompletedTask;

        public IChatClient Chat(ModelId id)
        {
            Interlocked.Increment(ref _chatCalls);
            return new FakeChatClient(_answers, traceId => LastTraceId = traceId);
        }

        private sealed class FakeChatClient(string[] deltas, Action<string?> onTrace) : IChatClient
        {
            public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
                => throw new NotSupportedException();

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                onTrace(Activity.Current?.TraceId.ToString());
                await Task.Yield();
                foreach (var delta in deltas)
                {
                    yield return new ChatResponseUpdate(ChatRole.Assistant, delta);
                }
                yield return new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(new UsageDetails { InputTokenCount = 4, OutputTokenCount = 7 })]);
            }

            public object? GetService(Type serviceType, object? serviceKey = null) => null;
            public void Dispose() { }
        }
    }

    private sealed class ListLogger<T>(List<string> logs) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (logs) { logs.Add(formatter(state, exception)); }
        }
    }
}
