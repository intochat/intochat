using System.Collections.Concurrent;
using System.Diagnostics;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections.Broker;
using DigitalBrain.Capabilities.Connections.Connectors;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Capabilities.Connections.Requests;
using DigitalBrain.Capabilities.Connections.Sealing;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Contracts;
using DigitalBrain.Hosting;
using DigitalBrain.Kernel;
using DigitalBrain.Modules;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Runtime.Orleans.Testing;
using DigitalBrain.Screens;
using DigitalBrain.Signals;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Apps;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Apps.Settings;
using IntoChat.OS.Authority;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Connections;
using IntoChat.OS.Connections.Contracts;
using IntoChat.OS.Connections.Platform.Entries;
using IntoChat.OS.Connections.Platform.Secrets;
using IntoChat.OS.Connections.Surface;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Kernel;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.AI.Models.Ollama;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Views;
using IntoChat.OS.Modules.Microsoft.Aspire;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans;

namespace IntoChat.OS.Apps.Tests.Integration.Ino;

[Module("ino.wiring.integration")]
internal sealed class InoWiringTestModule : DigitalBrain.Modules.Module
{
    public static ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [],
        Settings: [],
        Secrets: []);
}

public sealed class SwappableChatModel : IChatClient
{
    private volatile Func<IReadOnlyList<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>> _responder;
    private readonly ConcurrentQueue<(ChatMessage[] Messages, ChatOptions? Options)> _requests = new();

    public SwappableChatModel()
    {
        _responder = static (_, _, _) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
    }

    public IReadOnlyList<(ChatMessage[] Messages, ChatOptions? Options)> Requests => [.. _requests];

    public void Reply(string answer)
        => _responder = (_, _, _) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, answer)));

    public void Respond(Func<IReadOnlyList<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>> responder)
        => _responder = responder ?? throw new ArgumentNullException(nameof(responder));

    public void Fail(Exception error)
        => _responder = (_, _, _) => Task.FromException<ChatResponse>(error);

    public HeldTurn Hold(string eventualAnswer)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _responder = async (_, _, ct) =>
        {
            entered.TrySetResult();
            var text = await release.Task.WaitAsync(ct);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, text));
        };
        return new HeldTurn(entered.Task, () => release.TrySetResult(eventualAnswer));
    }

    public void ClearRequests()
    {
        while (_requests.TryDequeue(out _))
        {
        }
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = messages.ToArray();
        _requests.Enqueue((snapshot, options));
        return _responder(snapshot, options, cancellationToken);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }
}

public sealed record HeldTurn(Task Entered, Action Release);

internal sealed class ControllableLocalOllamaProbe : ILocalOllamaProbe
{
    public LocalOllamaDetection? Current { get; set; }

    public Task<LocalOllamaDetection?> Detect(CancellationToken ct = default)
        => Task.FromResult(Current);
}

public sealed class InoSiloFixture : IAsyncLifetime
{
    private readonly FakeStorage _storage = new("Workspace Disk");
    private readonly IntoChat.OS.Tests.Support.TestHome _home = new("intochat-ino-silo-");
    private IInProcessHost? _host;
    private int _brainCounter;

    public SwappableChatModel Model { get; } = new();
    internal ControllableLocalOllamaProbe LocalOllama { get; } = new();

    public IServiceProvider Services => (_host ?? throw new InvalidOperationException("Silo host is not started.")).Services;

    public string NewBrain(string prefix = "inobrain")
        => $"{prefix}{Interlocked.Increment(ref _brainCounter)}";

    public IDisposable AsPerson(string brain)
        => RuntimeCallerContext.Stamp(CapabilityCallers.Of(brain, brain, brain));

    public IScreen InoScreen(string brain)
        => SiloNeurons.Get<IScreen>(Services, new NeuronId(new BrainId(brain), "app.ino", "default"));

    public IGrainFactory Grains => Services.GetRequiredService<IGrainFactory>();

    public static async Task<PublishedView> WaitForScreenAsync(
        IScreen screen,
        Func<PublishedView, bool> predicate,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        PublishedView? last = null;
        while (sw.Elapsed < TimeSpan.FromSeconds(10))
        {
            last = await screen.Read();
            if (last is not null && predicate(last))
            {
                return last;
            }
            await Task.Delay(35, ct);
        }

        var texts = last is null ? "<null>" : string.Join(" | ", PresentedView.From(last).Texts);
        throw new TimeoutException($"Timed out waiting for Ino screen state. Last texts: [{texts}]");
    }

    public static bool TryGetConversation(PublishedView published, out Conversation conversation)
    {
        var view = System.Text.Json.JsonSerializer.Deserialize<View>(published.View, System.Text.Json.JsonSerializerOptions.Web);
        if (view?.Root is Conversation conv)
        {
            conversation = conv;
            return true;
        }

        conversation = null!;
        return false;
    }

    public static Conversation RequireConversation(PublishedView published)
    {
        var view = System.Text.Json.JsonSerializer.Deserialize<View>(published.View, System.Text.Json.JsonSerializerOptions.Web);
        Assert.NotNull(view);
        return Assert.IsType<Conversation>(view.Root);
    }

    public async ValueTask InitializeAsync()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [IntoChatOSNames.HomeKey] = _home.Path,
                ["MasterKey:MasterKey"] = "0123456789abcdef0123456789abcdef",
            })
            .Build();

        var signalAssemblies = new[]
        {
            typeof(Activated).Assembly,
            typeof(InoWiringTestModule).Assembly,
            typeof(AppsModule).Assembly,
            typeof(InoApp).Assembly,
            typeof(IApp).Assembly,
            typeof(IntoChat.OS.Modules.Apps.Signals.AppRequested).Assembly,
            typeof(IdentityModule).Assembly,
            typeof(AuthorityPlainNeurons).Assembly,
            typeof(IConnectionRequests).Assembly,
            typeof(AIModule).Assembly,
            typeof(IMemory).Assembly,
            typeof(AspireModule).Assembly,
            typeof(IAspire).Assembly,
            typeof(FlutterModule).Assembly,
            typeof(IView).Assembly,
        }.Distinct().ToArray();

        var factory = new LocalSiloHostFactory(
            new BrainStorageRegistration(signalAssemblies),
            services =>
            {
                AppsModule.ConfigureServices(services, config);
                AIModule.ConfigureServices(services, config);
                services.AddIntoChatOSConnections();
                services.AddNeuronProxies();
                services.RemoveAll<IBrainObserver>();
                services.AddSingleton<IBrainObserver, CompiledBrainObserver>();
                services.RemoveAll<NeuronSurface>();
                services.AddSingleton(sp => new NeuronSurface(
                    sp.GetRequiredService<INeuronProxies>(),
                    sp.GetRequiredService<IBrainObserver>(),
                    sp.GetRequiredService<NeuronTypeSearch>(),
                    plain: sp));
                services.AddOperatingSystemApps();

                foreach (var d in services.Where(d => d.ImplementationType?.Name is "FeatureBootLifecycle").ToList())
                {
                    services.Remove(d);
                }

                services.RemoveAll<IChatClient>();
                services.AddSingleton<IChatClient>(Model);

                services.RemoveAll<IAiCredentials>();
                services.AddSingleton<IAiCredentials, NoConfiguredProviders>();

                services.RemoveAll<ILocalOllamaProbe>();
                services.AddSingleton<ILocalOllamaProbe>(LocalOllama);

                services.RemoveAll<ISecretRedactor>();
                services.AddSingleton<ISecretRedactor, NoOpSecretRedactor>();

                services.TryAddSingleton<IReadOnlyList<IntegrationSchema>>(AIModule.Integrations);
                services.TryAddSingleton<IBrainSecretSealer, TestBrainSecretSealer>();

                services.AddSecretsCapability<VaultPort>();
                services.AddIdentityCapability<TestIdentityDirectoryPort>();
                services.AddSingleton<ICapabilityConnections>(
                    new SingleStorageConnections("local-disk", "Workspace Disk", _storage));
            });

        _host = await factory.Start(
            new InProcessHostOptions
            {
                Modules =
                [
                    typeof(InoWiringTestModule),
                    typeof(ShippedShellAppsModule),
                    typeof(AppsModule),
                    typeof(IdentityModule),
                    typeof(AuthorityPlainNeurons),
                    typeof(AIModule),
                    typeof(AspireModule),
                    typeof(FlutterModule),
                ],
            },
            CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }

        _home.Dispose();
    }

    private sealed class NoOpSecretRedactor : ISecretRedactor
    {
        public ValueTask<string?> Redact(string? text, CancellationToken cancellationToken)
            => ValueTask.FromResult(text);

        public ValueTask<int> TailLength(CancellationToken cancellationToken)
            => ValueTask.FromResult(0);
    }

    private sealed class CompiledBrainObserver : IBrainObserver
    {
        private static readonly BrainType[] HostedTypes = new CompiledTypes(
        [
            typeof(IMemory).Assembly,
            typeof(IUserSettings).Assembly,
            typeof(IAspire).Assembly,
        ]).Entries;

        public Task<BrainType[]> Types(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(HostedTypes);
        }

        public IAsyncEnumerable<BrainInstance> Instances<T>(CancellationToken cancellationToken = default) where T : class
            => EmptyInstances(cancellationToken);

        public IAsyncEnumerable<BrainInstance> Instances(string? type, CancellationToken cancellationToken = default)
        {
            _ = type;
            return EmptyInstances(cancellationToken);
        }

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

        public Task<ObservedSynapse[]> Synapses(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Array.Empty<ObservedSynapse>());
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
            _ = id;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<System.Text.Json.JsonElement?>(null);
        }

        private static async IAsyncEnumerable<BrainInstance> EmptyInstances([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }

        private static async IAsyncEnumerable<SignalDelivery> EmptyDeliveries([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class NoConfiguredProviders : IAiCredentials
    {
        public Task<IntegrationSnapshot> StatusOf(string integrationId, CancellationToken cancellationToken = default)
            => Task.FromResult(new IntegrationSnapshot { IntegrationId = integrationId, Status = IntegrationStatus.Unconfigured });

        public Task<long> GenerationOf(string integrationId, CancellationToken cancellationToken = default) => Task.FromResult(0L);

        public Task<ReleasedIntegration> MaterializeAsync(string integrationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string> ReleaseSecretAsync(string integrationId, string field, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string?> BrainKey(string brainId, AiProvider provider, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    private sealed class TestBrainSecretSealer : IBrainSecretSealer
    {
        public Task<SealedSecret> Seal(string brainId, string name, string plaintext)
            => Seal(brainId, name, plaintext, CancellationToken.None);

        public Task<SealedSecret> Seal(string brainId, string name, string plaintext, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new SealedSecret
            {
                KeyId = BrainSeal.KeyPrefix + brainId,
                Cipher = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext)),
            });
        }

        public Task<string> Open(string brainId, SealedSecret secret)
            => Open(brainId, secret, CancellationToken.None);

        public Task<string> Open(string brainId, SealedSecret secret, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(secret.Cipher)));
        }
    }

    private sealed class SingleStorageConnections(string key, string label, IStorage storage) : ICapabilityConnections
    {
        public Task<IReadOnlyList<BrainConnection>> ListInOrder(BrainId brain, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<BrainConnection>>(
                [new BrainConnection(key, label, true, [Capability.Storage.V1], null)]);

        public Task<TCapability> Bind<TCapability>(BrainId brain, string connectionKey, string callerApp, CancellationToken ct = default)
            where TCapability : class
            => Task.FromResult((TCapability)(object)storage);

        public Task RequestConnection(BrainId brain, CapabilityId capability, string callerApp, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class TestIdentityDirectoryPort : IIdentityDirectoryPort
    {
        public Task<Account?> ReadAccount(string accountId, CancellationToken cancellationToken)
            => Task.FromResult<Account?>(new Account { AccountId = accountId, Name = accountId, OwnerPrincipalId = accountId, CreatedAt = DateTimeOffset.UtcNow });

        public Task<Account?> OwnerOfBrain(string brainId, CancellationToken cancellationToken)
            => Task.FromResult<Account?>(new Account { AccountId = brainId, Name = brainId, OwnerPrincipalId = brainId, CreatedAt = DateTimeOffset.UtcNow });

        public Task EnsureAccount(Account account, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<BrainProvision> CreateBrain(string accountId, string principalId, string operationId, string displayName, BrainKind kind, CancellationToken cancellationToken)
            => Task.FromResult(new BrainProvision(new Member { PrincipalId = principalId, AccountId = accountId, BrainId = accountId, Role = MemberRole.Owner, DisplayName = displayName, JoinedAt = DateTimeOffset.UtcNow }, true));

        public Task<BrainProvision> EnsureBrain(Member owner, CancellationToken cancellationToken)
            => Task.FromResult(new BrainProvision(owner, false));

        public Task<BrainProvision> ProvisionOwner(Member owner, CancellationToken cancellationToken)
            => Task.FromResult(new BrainProvision(owner, true));

        public Task<bool> IsRegistered(string principalId, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<BrainProvision> RegisterPrincipal(string principalId, string displayName, CancellationToken cancellationToken)
            => Task.FromResult(new BrainProvision(new Member { PrincipalId = principalId, AccountId = principalId, BrainId = principalId, Role = MemberRole.Owner, DisplayName = displayName, JoinedAt = DateTimeOffset.UtcNow }, false));

        public Task<BrainProvision?> CompleteRegistration(string principalId, CancellationToken cancellationToken)
            => Task.FromResult<BrainProvision?>(null);

        public Task<IReadOnlyList<DigitalBrain.Capabilities.Identity.BrainEntry>> BrainsOf(string principalId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<DigitalBrain.Capabilities.Identity.BrainEntry>>([new DigitalBrain.Capabilities.Identity.BrainEntry(principalId, BrainKind.Personal)]);

        public Task<BrainKind> KindOf(string brainScopeId, CancellationToken cancellationToken) => Task.FromResult(BrainKind.Personal);

        public Task<bool> BrainExists(string brainScopeId, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<Member?> Membership(string brainScopeId, string principalId, CancellationToken cancellationToken)
            => Task.FromResult<Member?>(new Member { PrincipalId = principalId, AccountId = principalId, BrainId = brainScopeId, Role = MemberRole.Owner, DisplayName = principalId, JoinedAt = DateTimeOffset.UtcNow });

        public Task<Member?> EnsureMember(CapabilityCaller caller, Member member, CancellationToken cancellationToken)
            => Task.FromResult<Member?>(member);

        public Task RemoveMember(CapabilityCaller caller, string brainScopeId, string accountId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RemovePrincipalMember(CapabilityCaller caller, string brainScopeId, string principalId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<DigitalBrain.Capabilities.Identity.CallDecision?> Authorize(CapabilityCaller caller, DigitalBrain.Capabilities.Identity.CallRequest request, CancellationToken cancellationToken)
            => Task.FromResult<DigitalBrain.Capabilities.Identity.CallDecision?>(new DigitalBrain.Capabilities.Identity.CallDecision { Allowed = true });

        public Task<IReadOnlyList<DigitalBrain.Capabilities.Identity.Grant>> ListGrants(string brainScopeId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<DigitalBrain.Capabilities.Identity.Grant>>([]);

        public Task<DigitalBrain.Capabilities.Identity.Grant?> GrantAsOwner(CapabilityCaller caller, DigitalBrain.Capabilities.Identity.Grant grant, CancellationToken cancellationToken)
            => Task.FromResult<DigitalBrain.Capabilities.Identity.Grant?>(grant);

        public Task<bool> RevokeAsOwner(CapabilityCaller caller, string appId, string semanticTypeId, DigitalBrain.Capabilities.Identity.GrantMode mode, CancellationToken cancellationToken)
            => Task.FromResult(true);
    }
}
