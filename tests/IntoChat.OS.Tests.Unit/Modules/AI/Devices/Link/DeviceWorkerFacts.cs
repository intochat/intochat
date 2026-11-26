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

    private static async Task<Harness> Open(
        string[] ready,
        string[]? runtimeAnswers = null,
        bool joined = true)
    {
        var tempRoot = new DigitalBrain.Testing.TempDirectory("intochat-device-worker-facts-");
        var root = tempRoot.Path;
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero));
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
        var worker = new DeviceWorker(cloud, credentials, [runtime], machine, logger, clock, () => "vlad-pc");

        return new Harness(worker, server, runtime, logs, http, tempRoot);
    }

    private sealed class Harness(
        DeviceWorker worker,
        FakeRelayServer server,
        FakeLocalRuntime runtime,
        List<string> logs,
        HttpClient http,
        DigitalBrain.Testing.TempDirectory tempRoot) : IAsyncDisposable
    {
        public DeviceWorker Worker => worker;
        public FakeRelayServer Server => server;
        public FakeLocalRuntime Runtime => runtime;
        public IReadOnlyList<string> Logs => logs;

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

    private sealed class FakeRelayServer : HttpMessageHandler
    {
        private readonly Queue<DeviceChatJob> _jobs = new();
        private readonly Dictionary<string, List<DeviceChunk>> _streams = new(StringComparer.Ordinal);
        public List<string> RequestPaths { get; } = [];
        public ModelsReport? LastReport { get; private set; }

        public void Enqueue(DeviceChatJob job) => _jobs.Enqueue(job);

        public IReadOnlyList<DeviceChunk> Stream(string jobId)
            => _streams.TryGetValue(jobId, out var chunks) ? chunks : [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            RequestPaths.Add(path);

            if (path == DeviceRoutes.Models && request.Method == HttpMethod.Put)
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                LastReport = JsonSerializer.Deserialize(body, DeviceWireJson.Default.ModelsReport);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (path == DeviceRoutes.Next && request.Method == HttpMethod.Get)
            {
                if (_jobs.Count == 0)
                {
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                }

                var job = _jobs.Dequeue();
                var json = JsonSerializer.Serialize(job, DeviceWireJson.Default.DeviceChatJob);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
                };
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

        public FakeLocalRuntime(string[] ready, string[] answers)
        {
            _answers = answers;
            foreach (var id in ready)
            {
                AddInstalled(id);
            }
        }

        public RuntimeId Id => RuntimeId.Ollama;
        public int ChatCalls { get; private set; }
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
            ChatCalls++;
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
