#pragma warning disable MEAI001 // Deterministic local voice/STT test doubles for /ai/talk and /ai/attachments E2E journeys.
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Identity.Local.Directory;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.Aspire.Screens;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Kernel;
using IntoChat.OS.Kernel.Composition.Kernel;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.AI.Voice;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Identity.Accounts;
using IntoChat.OS.Modules.Identity.Authority;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using IntoChat.OS.Modules.Microsoft.Roslyn;
using IntoChat.OS.Tests.Browser.Scenarios;
using IntoChat.OS.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using Orleans.Hosting;
using Orleans.Runtime;
using Yarp.ReverseProxy.Transforms;
using ModuleConfiguration = DigitalBrain.Modules.Configuration.ModuleConfiguration;

namespace IntoChat.OS.Tests.Browser;

[CollectionDefinition(nameof(Item350WebShellCollection))]
public sealed class Item350WebShellCollection : ICollectionFixture<Item350WebShellFixture>;

/// <summary>
/// Shared in-process OS Kernel + Orleans Silo + released Flutter web bundle fixture for Item #350 (INT-2507)
/// nightly Playwright E2E journeys. Uses deterministic local fakes only for external AI/voice providers and
/// sandbox execution while running the real ASP.NET Core endpoints, Orleans grains, Roslyn AST sanitizer,
/// SecretRedactor, Flutter Web shell, and Chromium browser.
/// </summary>
public sealed class Item350WebShellFixture(WarmBrowserFixture browser) : IAsyncLifetime
{
    private static readonly string[] DisabledModules =
    [
        "artcraft",
        "playwright",
        "postgres",
        "clickhouse",
        "azure",
        "sandbox",
        "webtools",
    ];

    /// <summary>Synthetic canary secret registered with <see cref="ISecretValueSource"/> to verify real <see cref="ISecretRedactor"/> masking.</summary>
    public static readonly string SyntheticSecretValue = string.Concat("ITEM350-", "CANARY-", "SECRET-", "9876543210");

    private readonly TestHome _home = new();
    private readonly TestBrainOwners _brainOwners = new();
    private WebApplication? _kernelApp;
    private WebApplication? _origin;
    private string _kernelOrigin = string.Empty;
    private string _presence = string.Empty;

    public string Origin { get; private set; } = string.Empty;

    public string KernelOrigin => _kernelOrigin;

    public string PresenceToken => _presence;

    public string Scope { get; } = BrainScope.Create(AccountSession.DefaultLogin, AccountSession.DefaultLogin).Id;

    public DeterministicModelInference Inference { get; } = new(SyntheticSecretValue);

    public DeterministicSandboxBridge Sandbox { get; } = new();

    public ConcurrentDictionary<string, EngineerRunSummary> EngineerRuns { get; } = new(StringComparer.Ordinal);

    public IServiceProvider Services => (_kernelApp ?? throw new InvalidOperationException("Kernel host is not started.")).Services;

    public string Shell => $"{Origin}/?semantics=true#presence={Uri.EscapeDataString(_presence)}";

    public string ShellFor(string root) => $"{Origin}/?root={Uri.EscapeDataString(root)}&semantics=true#presence={Uri.EscapeDataString(_presence)}";

    public Task<IBrowserContext> NewBrowserContextAsync(int width, int height, CancellationToken ct = default)
        => browser.NewContextAsync(width, height, ct);

    public HttpClient CreateKernelClient(bool authenticated = true)
    {
        var client = new HttpClient { BaseAddress = new Uri(_kernelOrigin) };
        if (authenticated)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation(PresenceProof.HeaderName, _presence);
        }

        return client;
    }

    public HttpClient CreateOriginClient(bool authenticated = true)
    {
        var client = new HttpClient { BaseAddress = new Uri(Origin) };
        if (authenticated)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation(PresenceProof.HeaderName, _presence);
        }

        return client;
    }

    public IDisposable StampOwner() => StampPerson(AccountSession.DefaultLogin, Scope);

    public void ProvisionPersonalBrain(string account, string brainId)
    {
        _brainOwners.Register(brainId, account);
        var mirror = Services.GetRequiredService<AuthorityMirror>();
        mirror.Publish(brainId, new DirectoryBrainSnapshot
        {
            BrainId = brainId,
            AccountId = account,
            Members =
            [
                new Member
                {
                    PrincipalId = account,
                    AccountId = account,
                    BrainId = brainId,
                    Role = MemberRole.Owner,
                    DisplayName = account,
                },
            ],
            OwnerProvisioned = true,
            Kind = BrainKind.Personal,
        });
    }

    public IDisposable StampPerson(string account, string brainId)
    {
        RequestContext.Set(KernelInvocationSink.CallerKey, "");
        var edge = Services.GetRequiredService<EdgeAuthority>();
        return edge.Enter(edge.Person(account, account, new BrainId(brainId)));
    }

    public async ValueTask InitializeAsync()
    {
        try
        {
            var bundle = await WebBundle.Ensure(TestHome.RepoRoot(), "apps/gallery/view");
            IntoChat.OS.Host.Modules.OsModule.InitializeConfigurationRoot();

            var originPort = FreePort();
            var kernelPort = FreePort();
            var siloPort = FreePort();

            Origin = $"http://127.0.0.1:{originPort}";
            _kernelOrigin = $"http://127.0.0.1:{kernelPort}";
            var aspireDashboardUrl = $"{Origin}/aspire-dashboard-stub";

            List<string> args =
            [
                $"--{IntoChatOSNames.HomeKey}={_home.Path}",
                $"--{IntoChatOSNames.TopologyKey}={IntoChatOSNames.TopologyLocal}",
                $"--{IntoChatOSNames.ShellEnabledKey}=false",
                $"--{IntoChatOSNames.FrontEnabledKey}=false",
                $"--{ModuleConfiguration.ModuleSection("devices")}:ReplaceOptions=true",
                $"--{ModuleConfiguration.ModuleSection("devices")}:Options:FoundryLocal:Enabled=false",
                $"--{ModuleConfiguration.ModuleSection("devices")}:Options:Ollama:Enabled=false",
                $"--{AspireAppState.ConfiguredUrlKey}={aspireDashboardUrl}",
                $"--{TopologyContext.SiloPortKey}={siloPort.ToString(CultureInfo.InvariantCulture)}",
                $"--urls={_kernelOrigin}",
            ];
            args.AddRange(DisabledModules.Select(module => $"--{ModuleConfiguration.ModuleSection(module)}:Enabled=false"));

            var kernelBuilder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = [.. args],
                EnvironmentName = "Development",
                ApplicationName = typeof(KernelHost).Assembly.GetName().Name,
                ContentRootPath = Path.GetTempPath(),
            });
            kernelBuilder.WebHost.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");
            kernelBuilder.WebHost.UseUrls(_kernelOrigin);
            kernelBuilder.Logging.ClearProviders();

            _brainOwners.Register(Scope, AccountSession.DefaultLogin);
            var chatClient = new DeterministicChatClient();
            var voiceClients = new DeterministicVoiceClients(SyntheticSecretValue);
            var secretSource = new StaticSecretValueSource(SyntheticSecretValue);
            var astSanitizer = new CodeOrchestrationAstSanitizer();

            kernelBuilder.AddIntoChatOSKernel(typeof(KernelHost).Assembly);
            RegisterDeterministicOverrides(kernelBuilder.Services, chatClient, Inference, Sandbox, voiceClients, secretSource, astSanitizer, _brainOwners);
            kernelBuilder.Host.UseOrleans(silo =>
            {
                RegisterDeterministicOverrides(silo.Services, chatClient, Inference, Sandbox, voiceClients, secretSource, astSanitizer, _brainOwners);
                silo.AddIncomingGrainCallFilter(new DeterministicEngineerRunsFilter(EngineerRuns));
            });

            _kernelApp = kernelBuilder.Build();
            _kernelApp.MapIntoChatOSKernel();

            using var start = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await _kernelApp.StartAsync(start.Token);
            _presence = await TestHome.ReadPresenceTokenAsync(_home.PresenceFilePath, start.Token, TimeSpan.FromMinutes(2));
            await StartOriginAsync(bundle, _kernelOrigin, start.Token);
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_origin is not null)
        {
            try { await _origin.DisposeAsync(); } catch { /* best effort */ }
        }

        if (_kernelApp is not null)
        {
            try
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await _kernelApp.StopAsync(stop.Token);
            }
            catch { /* best effort */ }

            try { await _kernelApp.DisposeAsync(); } catch { /* best effort */ }
        }

        _home.Dispose();
    }

    private static void RegisterDeterministicOverrides(
        IServiceCollection services,
        IChatClient chatClient,
        IModelInference inference,
        ISandboxRuntimeBridge sandbox,
        IVoiceClients voiceClients,
        ISecretValueSource secretSource,
        ICodeOrchestrationAstSanitizer astSanitizer,
        IBrainOwners brainOwners)
    {
        services.Replace(ServiceDescriptor.Singleton(chatClient));
        services.Replace(ServiceDescriptor.Singleton(inference));
        services.Replace(ServiceDescriptor.Singleton(sandbox));
        services.Replace(ServiceDescriptor.Singleton(voiceClients));
        services.Replace(ServiceDescriptor.Singleton(astSanitizer));
        services.Replace(ServiceDescriptor.Singleton(brainOwners));
        services.AddSingleton(secretSource);
    }

    private async Task StartOriginAsync(string bundle, string kernel, CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(Origin);
        builder.Logging.ClearProviders();
        builder.Services.AddHttpForwarder();
        var app = builder.Build();
        var config = JsonSerializer.Serialize(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["INTOCHATOS_UI_BASE"] = Origin,
            ["INTOCHATOS_SHELL"] = "desk",
            ["INTOCHATOS_CHAT"] = "main",
        });

        app.Use((context, next) => context.Request.Path == "/config.json"
            ? Results.Text(config, "application/json").ExecuteAsync(context)
            : next(context));
        app.Use((context, next) => context.Request.Path.StartsWithSegments("/aspire-dashboard-stub", StringComparison.OrdinalIgnoreCase)
            ? Results.Text(
                $"<!doctype html><html><head><title>.NET Aspire Dashboard</title></head><body><h1>.NET Aspire Dashboard</h1><p>{WebUtility.HtmlEncode(context.Request.Path.Value)}</p></body></html>",
                "text/html").ExecuteAsync(context)
            : next(context));

        var files = new PhysicalFileProvider(bundle);
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = files, ServeUnknownFileTypes = true });
        app.UseRouting();
        app.MapForwarder("/{**catch-all}", kernel, transforms => transforms.AddOriginalHost(true));
        await app.StartAsync(ct);
        _origin = app;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class DeterministicChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var reply = BuildReply(messages);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply))
            {
                ModelId = "test/item350",
            });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var reply = BuildReply(messages);
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, reply)
            {
                FinishReason = ChatFinishReason.Stop,
                ModelId = "test/item350",
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }

        private static string BuildReply(IEnumerable<ChatMessage> messages)
        {
            var latestUser = messages.LastOrDefault(static m => m.Role == ChatRole.User)?.Text ?? string.Empty;
            return latestUser.Contains("[Active context:", StringComparison.Ordinal)
                ? "Applied your follow-up iteration to the active artifact: added a reset button."
                : "Received your message and attachments cleanly.";
        }
    }

    private sealed class TestBrainOwners : IBrainOwners
    {
        private readonly ConcurrentDictionary<string, string> _owners = new(StringComparer.Ordinal);

        public void Register(string brainId, string account) => _owners[brainId] = account;

        public Task<string?> OwnerAccount(BrainId brain, CancellationToken ct = default)
            => Task.FromResult(_owners.TryGetValue(brain.Value, out var account) ? account : null);
    }

    private sealed class StaticSecretValueSource(string secret) : ISecretValueSource
    {
        public ValueTask<IReadOnlyList<string>> CurrentValues(CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<string>>([secret]);
    }

    public sealed class DeterministicModelInference(string secretCanary) : IModelInference
    {
        private int _roomCallCount;
        private int _inoCallCount;

        public ConcurrentQueue<InferenceRequest> RoomRequests { get; } = new();

        public ConcurrentQueue<InferenceRequest> InoRequests { get; } = new();

        public int RoomCallCount => Volatile.Read(ref _roomCallCount);

        public int InoCallCount => Volatile.Read(ref _inoCallCount);

        public void ResetRoomCounters()
        {
            Interlocked.Exchange(ref _roomCallCount, 0);
            while (RoomRequests.TryDequeue(out _)) { }
        }

        public bool HasProfile(string name) => true;

        public Task<ModelDescriptor> Describe(ModelSelection? selection, ModelId? model, CancellationToken cancellationToken, string? brainId)
            => Task.FromResult(new ModelDescriptor(
                Provider: "deterministic",
                Model: model?.Value ?? "test/item350",
                Profile: "default",
                Revision: "1",
                Tools: CapabilitySupport.Supported,
                Vision: CapabilitySupport.Supported,
                StructuredOutput: CapabilitySupport.Supported));

        public Task<InferenceResult> Generate(InferenceRequest request, ModelId? model, CancellationToken cancellationToken, string? brainId)
        {
            var reply = BuildReply(request);
            return Task.FromResult(new InferenceResult(
                Messages: [new AiMessage("assistant", [new AiText(reply)])],
                FinishReason: "stop",
                Usage: new InferenceUsage(16, 20, 36),
                ResponseId: Guid.NewGuid().ToString("N"),
                Model: model?.Value ?? "test/item350"));
        }

        public async IAsyncEnumerable<InferenceUpdate> GenerateStreaming(
            InferenceRequest request,
            ModelId? model,
            [EnumeratorCancellation] CancellationToken cancellationToken,
            string? brainId)
        {
            var reply = BuildReply(request);
            await Task.Yield();
            yield return new InferenceUpdate(
                Role: "assistant",
                Content: [new AiText(reply)],
                FinishReason: "stop",
                Usage: new InferenceUsage(16, 20, 36),
                ResponseId: Guid.NewGuid().ToString("N"),
                Model: model?.Value ?? "test/item350");
        }

        private string BuildReply(InferenceRequest request)
        {
            var systemText = string.Concat(request.Messages
                .Where(static m => string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase))
                .SelectMany(static m => m.Content)
                .OfType<AiText>()
                .Select(static t => t.Text));

            if (systemText.Contains("@Ino · Room only", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _roomCallCount);
                RoomRequests.Enqueue(request);
                return $"Room summary for everyone: weekend plan confirmed (redacted token check: {secretCanary}).";
            }

            Interlocked.Increment(ref _inoCallCount);
            InoRequests.Enqueue(request);
            var latestUser = string.Concat(request.Messages
                .LastOrDefault(static m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))
                ?.Content.OfType<AiText>().Select(static t => t.Text) ?? []);

            if (latestUser.Contains("[Active context:", StringComparison.Ordinal))
            {
                return "Applied your follow-up iteration to the active artifact: added a reset button.";
            }

            return "Your request has been processed cleanly.";
        }
    }

    public sealed class DeterministicSandboxBridge : ISandboxRuntimeBridge
    {
        public ConcurrentQueue<(string BrainId, string AppKey, string[] AllowedContracts)> ScopedDeployments { get; } = new();

        public ConcurrentQueue<(string BrainId, string AppKey, string Command, string[] AllowedContracts)> ScopedExecutions { get; } = new();

        public Task<SandboxDeploymentResult> DeployPackageAsync(
            string brainId,
            AppPackage package,
            byte[] assemblyBytes,
            string? stateJson,
            string? settingsJson,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new SandboxDeploymentResult(true, null, [], [], [], null));

        public Task<SandboxDeploymentResult> DeployScopedPackageAsync(
            BrainId brainId,
            AppPackage package,
            byte[] assemblyBytes,
            string? stateJson,
            string? settingsJson,
            IReadOnlyList<string> allowedContracts,
            CancellationToken cancellationToken = default)
        {
            ScopedDeployments.Enqueue((brainId.Value, package.Key, [.. allowedContracts]));
            return Task.FromResult(new SandboxDeploymentResult(true, null, [], [], [], null));
        }

        public Task<SandboxDispatchResult> ExecuteScopedCommandAsync(
            BrainId brainId,
            AppPackage package,
            byte[] assemblyBytes,
            string command,
            string? payloadJson,
            IReadOnlyList<string> allowedContracts,
            CancellationToken cancellationToken = default)
        {
            ScopedExecutions.Enqueue((brainId.Value, package.Key, command, [.. allowedContracts]));
            return Task.FromResult(new SandboxDispatchResult(
                Handled: true,
                ResultJson: """{"status":"succeeded","summary":"Tip Calculator compiled and verified in sandbox.","artifacts":[{"name":"tip-calculator.json","kind":"json","content":"{}"}],"metrics":{"checks":1}}""",
                UpdatedStateJson: null,
                UpdatedView: null,
                Error: null,
                ExecutionTarget: "IntoChat.OS.Sandbox"));
        }

        public Task<SandboxDispatchResult> DispatchSignalAsync(string brainId, string appKey, string signalName, string payloadJson, CancellationToken cancellationToken = default)
            => Task.FromResult(new SandboxDispatchResult(true, "{}", null, null, null, "IntoChat.OS.Sandbox"));

        public Task<SandboxDispatchResult> DispatchScheduleAsync(string brainId, string appKey, string cron, CancellationToken cancellationToken = default)
            => Task.FromResult(new SandboxDispatchResult(true, "{}", null, null, null, "IntoChat.OS.Sandbox"));

        public Task<SandboxDispatchResult> DispatchCommandAsync(string brainId, string appKey, string command, string? payloadJson = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new SandboxDispatchResult(true, "{}", null, null, null, "IntoChat.OS.Sandbox"));

        public Task<DigitalBrain.UI.View?> RenderAsync(string brainId, string appKey, CancellationToken cancellationToken = default)
            => Task.FromResult<DigitalBrain.UI.View?>(null);

        public Task ForwardSignalAsync(string brainId, string signalName, string payloadJson, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task UnloadAsync(string brainId, string appKey, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class DeterministicVoiceClients(string secretCanary) : IVoiceClients
    {
        public Task<Voice<ISpeechToTextClient>> Hear(string brain, CancellationToken ct)
            => Task.FromResult(new Voice<ISpeechToTextClient>(
                new DeterministicSpeechToTextClient(secretCanary),
                new ModelId("test/stt"),
                Unavailable: null));

        public Task<Voice<ITextToSpeechClient>> Speak(string brain, CancellationToken ct)
            => Task.FromResult(new Voice<ITextToSpeechClient>(null, null, "TTS unavailable in deterministic E2E harness."));

        public Task<TalkVoice> Talk(string brain, ConversationId conversation, CancellationToken ct)
            => Task.FromResult(new TalkVoice(
                new NoopRealtimeClient(),
                Model: null,
                RecordsItself: true,
                InputRate: 16_000,
                Unavailable: null));
    }

    private sealed class DeterministicSpeechToTextClient(string secretCanary) : ISpeechToTextClient
    {
        public Task<SpeechToTextResponse> GetTextAsync(Stream audioSpeechStream, SpeechToTextOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new SpeechToTextResponse($"Voice note transcript with sensitive key {secretCanary} redacted safely."));

        public async IAsyncEnumerable<SpeechToTextResponseUpdate> GetStreamingTextAsync(
            Stream audioSpeechStream,
            SpeechToTextOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class NoopRealtimeClient : IRealtimeClient
    {
        public Task<IRealtimeClientSession> CreateSessionAsync(RealtimeSessionOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IRealtimeClientSession>(new NoopSession(options));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }

        private sealed class NoopSession(RealtimeSessionOptions? options) : IRealtimeClientSession
        {
            private readonly Channel<RealtimeServerMessage> _channel = Channel.CreateUnbounded<RealtimeServerMessage>();

            public RealtimeSessionOptions? Options => options;

            public Task SendAsync(RealtimeClientMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;

            public async IAsyncEnumerable<RealtimeServerMessage> GetStreamingResponseAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await foreach (var item in _channel.Reader.ReadAllAsync(cancellationToken))
                {
                    yield return item;
                }
            }

            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public ValueTask DisposeAsync()
            {
                _channel.Writer.TryComplete();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class DeterministicEngineerRunsFilter(ConcurrentDictionary<string, EngineerRunSummary> runs) : IIncomingGrainCallFilter
    {
        public Task Invoke(IIncomingGrainCallContext context)
        {
            if (context.Request.GetArgumentCount() > 0
                && context.Request.GetArgument(0) is DigitalBrain.Invocation.RuntimeInvocation call
                && string.Equals(call.Method, nameof(IEngineerRuns.InspectEngineerRun), StringComparison.Ordinal)
                && call.Key.Contains("/engineer.runs/", StringComparison.Ordinal)
                && call.Arguments.Length > 0
                && call.Arguments[0].ValueKind == JsonValueKind.String)
            {
                var flowId = call.Arguments[0].GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(flowId) && runs.TryGetValue(flowId, out var summary))
                {
                    context.Result = DigitalBrain.Invocation.RuntimeProtocol.SerializeToElement(summary, typeof(EngineerRunSummary));
                    return Task.CompletedTask;
                }
            }

            return context.Invoke();
        }
    }
}
