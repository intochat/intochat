using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Components;
using DigitalBrain.Kernel;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Http.Surface;
using DigitalBrain.Testing;
using IntoChat.OS.Host.Kernel;
using IntoChat.OS.Host.Modules;
using IntoChat.OS.Kernel;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Devices.Voice;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts;
using IntoChat.OS.Modules.Identity.Contracts.Codes;
using IntoChat.OS.Modules.Identity.Grants;
using IntoChat.OS.Modules.Microsoft.GitHub;
using IntoChat.OS.Modules.Microsoft.GitHub.Webhook;
using IntoChat.OS.Modules.Postgres.Tables;
using IntoChat.OS.Modules.Web;
using IntoChat.OS.Modules.Web.Search;
using IntoChat.OS.Tests.Support.Fakes.AI;
using IntoChat.OS.Tests.Support.Fakes.Devices;
using IntoChat.OS.Tests.Support.Fakes.GitHub;
using IntoChat.OS.Tests.Support.Fakes.Postgres;
using IntoChat.OS.Tests.Support.Fakes.Voice;
using IntoChat.OS.Tests.Support.Fakes.Web;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Tests.Support.Fakes;

/// <summary>
/// Extension methods that register IntoChat.OS module edge fakes into a <see cref="FakeSet"/>.
/// </summary>
public static class OsFakes
{
    /// <summary>Registers the in-memory <see cref="FakeTableStore"/> for the Postgres module.</summary>
    public static FakeSet Postgres(this FakeSet fakes)
    {
        ArgumentNullException.ThrowIfNull(fakes);
        return fakes
            .Add<ITableStore, FakeTableStore>(new())
            .Add<ITablePlacements, SharedTablePlacements>(new());
    }

    /// <summary>
    /// Registers in-memory friend codes, same-name brain ownership, <see cref="IFriendCheck"/> for cross-brain sharing, and an
    /// in-memory sealer for the brain's key pair, which the roster asks the identity module for.
    /// </summary>
    public static FakeSet Friends(this FakeSet fakes)
    {
        ArgumentNullException.ThrowIfNull(fakes);
        return fakes
            .Add<IBrainOwners, SameNameBrainOwners>(new())
            .Add<ICodeBook, InMemoryCodeBook>(new())
            .Configure(services =>
            {
                services.TryAddSingleton<IFriendCheck>(sp => new FriendsFriendCheck(sp.GetRequiredService<INeuronProxies>()));
                services.TryAddSingleton<DigitalBrain.Capabilities.Connections.Sealing.IBrainSecretSealer, InMemoryBrainSecretSealer>();
            });
    }

    /// <summary>
    /// Registers an in-memory <see cref="InMemoryGrantStore"/> and wires <see cref="GrantCallFilterStage"/> through
    /// <see cref="IAccessAdmissionStage"/> so grant and zero-token enforcement run on both TestOS and Silo.
    /// </summary>
    public static FakeSet Grants(this FakeSet fakes, params Grant[] initialGrants)
    {
        ArgumentNullException.ThrowIfNull(fakes);
        var store = new InMemoryGrantStore(initialGrants);
        return fakes
            .Add<InMemoryGrantStore, InMemoryGrantStore>(store)
            .Configure(services =>
            {
                services.AddSingleton<IAccessAdmissionStage>(sp =>
                    GrantAccessAdmissionStage.ForStore(store, sp.GetRequiredService<ICallerAmbient>()));
            });
    }

    /// <summary>Registers <see cref="FakeGitHub"/> and GitHub webhook ingress services for both TestOS and Silo.</summary>
    public static FakeSet GitHub(this FakeSet fakes, Action<FakeGitHub>? seed = null)
    {
        ArgumentNullException.ThrowIfNull(fakes);
        var github = new FakeGitHub();
        seed?.Invoke(github);
        return fakes
            .Add<IGitHubRepositorySource, FakeGitHub>(github)
            .Add<IGitHubUserAccess, FakeGitHub>(github)
            .Add<IGitHubInstallationVerifier, FakeGitHub>(github)
            .Add<IModuleIntegrations<GitHubModule>, FakeGitHub>(github)
            .Configure(services =>
            {
                DigitalBrain.Runtime.Orleans.BrainStorageHosting.AddJournalPayloads(
                    services,
                    typeof(DigitalBrain.Capabilities.Connections.ConnectionUpdated).Assembly);
                services.TryAddSingleton<GitHubAppRegistration>();
                services.TryAddSingleton<GitHubWebhookIngress>();
            });
    }

    /// <summary>Registers <see cref="FakeSearchAdapter"/> and configures web search to use it.</summary>
    public static FakeSet WebSearch(this FakeSet fakes, Action<FakeSearchAdapter>? seed = null)
    {
        ArgumentNullException.ThrowIfNull(fakes);
        var adapter = new FakeSearchAdapter();
        seed?.Invoke(adapter);
        return fakes
            .Add<ISearchAdapter, FakeSearchAdapter>(adapter)
            .Add<IOptions<WebModuleOptions>, OptionsWrapper<WebModuleOptions>>(
                new OptionsWrapper<WebModuleOptions>(new WebModuleOptions { Provider = SearchProvider.Searxng }));
    }

    /// <summary>Registers the Catalog services needed for <see cref="IAppStore"/> on both TestOS and Silo.</summary>
    public static FakeSet Catalog(this FakeSet fakes)
    {
        ArgumentNullException.ThrowIfNull(fakes);
        return fakes.Configure(services =>
        {
            services.AddLogging();
            services.AddSingleton(new CompiledAppsRegistration(typeof(IntoChat.OS.Apps.ShippedShellAppsModule).Assembly));
            foreach (var componentAssembly in OperatingSystemModules.ComponentAssemblies)
            {
                services.AddSingleton(new CompiledAppsRegistration(componentAssembly));
            }
            services.TryAddSingleton(FeatureCatalog.From);
            services.TryAddSingleton<FeatureLoader>();
            services.TryAddSingleton<MarketplaceView>();
            services.TryAddSingleton<ICapabilitySatisfaction, ConnectedModuleSatisfaction>();
            services.TryAddSingleton<AppInstaller>();
            services.RemoveAll<IBrainObserver>();
            services.AddSingleton<IBrainObserver, CompiledBrainObserver>();
        });
    }

    /// <summary>
    /// Registers a <see cref="ScriptedChatClient"/> driven by <paramref name="script"/> together with the
    /// AI module services needed to drive conversations and tool calls on both TestOS and Silo.
    /// </summary>
    public static FakeSet Llm(this FakeSet fakes, LlmScript script)
    {
        ArgumentNullException.ThrowIfNull(fakes);
        ArgumentNullException.ThrowIfNull(script);

        var client = new ScriptedChatClient(script);
        return fakes
            .Configure(services =>
            {
                AIModule.ConfigureServices(services, new ConfigurationBuilder().Build());
                DigitalBrain.Runtime.Orleans.BrainStorageHosting.AddJournalPayloads(
                    services,
                    typeof(IntoChat.OS.Modules.AI.Agents.IAgent).Assembly,
                    typeof(IntoChat.OS.Modules.Flutter.Conversations.ConversationChanged).Assembly);
                services.RemoveAll<BrainKeyAvailability>();
                services.RemoveAll<IBrainObserver>();
                services.AddSingleton<IBrainObserver, CompiledBrainObserver>();
                services.RemoveAll<NeuronTypeSearch>();
                services.RemoveAll<INeuronContractSearch>();
                services.AddSingleton(sp => new NeuronTypeSearch(
                    sp.GetRequiredService<IBrainObserver>(),
                    [],
                    embeddings: null,
                    callers: sp.GetService<ICallerAmbient>()));
                services.AddSingleton<INeuronContractSearch>(sp => sp.GetRequiredService<NeuronTypeSearch>());
                services.RemoveAll<NeuronSurface>();
                services.AddSingleton(sp => new NeuronSurface(
                    sp.GetRequiredService<INeuronProxies>(),
                    sp.GetRequiredService<IBrainObserver>(),
                    sp.GetRequiredService<INeuronContractSearch>(),
                    sp.GetService<IContractAssemblySource>(),
                    sp.GetService<ICallerAmbient>()));
            })
            .Add<LlmScript, LlmScript>(script)
            .Add<IChatClient, ScriptedChatClient>(client)
            .Add<IAiCredentials, NoConfiguredProviders>(new())
            .Add<ISecretRedactor, NoOpSecretRedactor>(new());
    }

    /// <summary>
    /// Registers a reusable in-memory <see cref="FakeLocalRuntime"/> in one line for local-model
    /// discovery, install, and inference tests without needing Ollama or Foundry Local running.
    /// </summary>
    public static FakeSet LocalRuntime(this FakeSet fakes, Action<FakeLocalRuntime>? seed = null)
    {
        ArgumentNullException.ThrowIfNull(fakes);
        var runtime = new FakeLocalRuntime();
        seed?.Invoke(runtime);
        return fakes.Configure(services =>
        {
            services.AddSingleton(runtime);
            services.AddSingleton<ILocalRuntime>(runtime);
        });
    }

    /// <summary>
    /// Registers deterministic <see cref="FakeSpeechToTextClient"/>, <see cref="FakeTextToSpeechClient"/>,
    /// <see cref="FakeVoiceActivityDetector"/>, and a voice-ready <see cref="FakeLocalRuntime"/> (Whisper + Kokoro)
    /// in one line for dictation, read-aloud, and full-duplex local voice-to-voice tests.
    /// </summary>
    public static FakeSet Voice(this FakeSet fakes, Func<string>? transcript = null, byte[]? audioPcm = null)
    {
        ArgumentNullException.ThrowIfNull(fakes);
        var stt = new FakeSpeechToTextClient(transcript);
        var tts = new FakeTextToSpeechClient(audioPcm);
        var vad = new FakeVoiceActivityDetector(ready: true);
        var voiceRuntime = new FakeLocalRuntime(new RuntimeId("fake-voice"))
        {
            SpeechToTextClient = stt,
            TextToSpeechClient = tts,
        }.WithInstalled(KnownModels.WhisperLargeV3Turbo, KnownModels.Kokoro82M);

        return fakes
            .Add<FakeSpeechToTextClient, FakeSpeechToTextClient>(stt)
            .Add<FakeTextToSpeechClient, FakeTextToSpeechClient>(tts)
            .Add<ISpeechToTextClient, FakeSpeechToTextClient>(stt)
            .Add<ITextToSpeechClient, FakeTextToSpeechClient>(tts)
            .Add<IVoiceActivityDetector, FakeVoiceActivityDetector>(vad)
            .Configure(services => services.AddSingleton<ILocalRuntime>(voiceRuntime));
    }

    private sealed class SameNameBrainOwners : IBrainOwners
    {
        public Task<string?> OwnerAccount(BrainId brain, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(brain.Value);
    }

    private sealed class InMemoryCodeBook : ICodeBook
    {
        private readonly ConcurrentDictionary<(CodePurpose Purpose, string Hash), CodeTarget> _codes = new();

        public Task<string> Bind(Code code, CodePurpose purpose, BrainId brain, string reference, DateTimeOffset expiresAt)
        {
            var hash = code.Value;
            _codes[(purpose, hash)] = new CodeTarget(brain, reference, expiresAt);
            return Task.FromResult(hash);
        }

        public Task<string> HashOf(Code code) => Task.FromResult(code.Value);

        public Task<CodeTarget?> Find(CodePurpose purpose, string hash)
            => Task.FromResult(_codes.TryGetValue((purpose, hash), out var target) ? target : null);

        public Task<CodeTarget?> Lookup(CodePurpose purpose, string hash) => Find(purpose, hash);

        public Task Spend(string hash)
        {
            foreach (var key in _codes.Keys.Where(k => k.Hash == hash).ToList())
            {
                _codes.TryRemove(key, out _);
            }
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpSecretRedactor : ISecretRedactor
    {
        public ValueTask<string?> Redact(string? text, CancellationToken cancellationToken)
            => ValueTask.FromResult(text);

        public ValueTask<int> TailLength(CancellationToken cancellationToken)
            => ValueTask.FromResult(0);
    }

    private sealed class NoConfiguredProviders : IAiCredentials
    {
        public Task<IntegrationSnapshot> StatusOf(string integrationId, CancellationToken cancellationToken = default)
            => Task.FromResult(new IntegrationSnapshot { IntegrationId = integrationId, Status = IntegrationStatus.Unconfigured });

        public Task<long> GenerationOf(string integrationId, CancellationToken cancellationToken = default)
            => Task.FromResult(0L);

        public Task<ReleasedIntegration> MaterializeAsync(string integrationId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<string> ReleaseSecretAsync(string integrationId, string field, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<string?> BrainKey(string brainId, AiProvider provider, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
    }

    private sealed class CompiledBrainObserver : IBrainObserver
    {
        private static readonly BrainType[] HostedTypes = new CompiledTypes(
            new ModuleInventory(OperatingSystemModules.Modules),
            [],
            [typeof(IntoChat.OS.Apps.ShippedShellAppsModule).Assembly, .. OperatingSystemModules.ComponentAssemblies]).Entries;

        public Task<BrainType[]> Types(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(HostedTypes);
        }

        public IAsyncEnumerable<BrainInstance> Instances<T>(CancellationToken cancellationToken = default) where T : class
            => EmptyInstances(cancellationToken);

        public IAsyncEnumerable<BrainInstance> Instances(string? type, CancellationToken cancellationToken = default)
            => EmptyInstances(cancellationToken);

        public IAsyncEnumerable<BrainInstance> InstancesIn(string brain, string? type, CancellationToken cancellationToken = default)
        {
            _ = brain;
            _ = type;
            return EmptyInstances(cancellationToken);
        }

        public Task<string[]> Brains(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Array.Empty<string>());
        }

        public Task<ObservedWire[]> Wires(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Array.Empty<ObservedWire>());
        }

        public IAsyncEnumerable<SignalDelivery> Deliveries(CancellationToken cancellationToken = default)
            => EmptyDeliveries(cancellationToken);

        public Task<BrainInstance[]> Running(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Array.Empty<BrainInstance>());
        }

        public Task<System.Text.Json.JsonElement?> State(NeuronId id, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<System.Text.Json.JsonElement?>(null);
        }

        private static async IAsyncEnumerable<BrainInstance> EmptyInstances([EnumeratorCancellation] CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }

        private static async IAsyncEnumerable<SignalDelivery> EmptyDeliveries([EnumeratorCancellation] CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }
    }
}
