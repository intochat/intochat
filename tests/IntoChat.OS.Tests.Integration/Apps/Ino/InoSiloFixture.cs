using System.Runtime.CompilerServices;
using DigitalBrain.Access;
using System.Collections.Concurrent;
using System.Diagnostics;
using DigitalBrain;
using DigitalBrain.Identity.Local.Machine;
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
using DigitalBrain.Invocation;
using DigitalBrain.UI;
using DigitalBrain.UI.Controls;
using DigitalBrain.Units;
using IntoChat.OS.Apps;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Apps.Settings;
using IntoChat.OS.Authority;
using IntoChat.OS.Kernel;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Views;
using IntoChat.Aspire;
using IntoChat.OS.Tests;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Host.Kernel;
using IntoChat.OS.Host.Surface;
using IntoChat.OS.Kernel.Platform;
using IntoChat.OS.Modules.Identity;
using IntoChat.OS.Modules.Identity.Ports;

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

internal sealed class ControllableLocalRuntime(IChatClient chatModel) : ILocalRuntime
{
    public RuntimeId Id => RuntimeId.Ollama;
    public bool Reachable { get; set; }
    public Uri? Endpoint { get; set; } = new("http://127.0.0.1:11434");
    public ModelCard[] InstalledCards { get; set; } = [];

    public Task<RuntimeStatus> Probe(CancellationToken ct)
        => Task.FromResult(new RuntimeStatus(Id, Reachable, Reachable ? "0.9.0" : null, Endpoint));

    public async IAsyncEnumerable<ModelCard> Catalog([EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        foreach (var card in InstalledCards) { yield return card; }
    }

    public async IAsyncEnumerable<ModelCard> Installed([EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        if (!Reachable) { yield break; }
        foreach (var card in InstalledCards) { yield return card; }
    }

    public async IAsyncEnumerable<InstallProgress> Install(ModelId id, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        yield break;
    }

    public Task Remove(ModelId id, CancellationToken ct) => Task.CompletedTask;

    public IChatClient Chat(ModelId id) => chatModel;
}

public sealed class InoSiloFixture : IAsyncLifetime
{
    private readonly FakeStorage _storage = new("Workspace Disk");
    private readonly IntoChat.OS.Tests.Support.TestHome _home = new("intochat-ino-silo-");
    private IInProcessHost? _host;
    private int _brainCounter;

    public SwappableChatModel Model { get; } = new();
    internal ControllableLocalRuntime LocalRuntime { get; }

    public InoSiloFixture()
    {
        LocalRuntime = new ControllableLocalRuntime(Model);
    }

    public IServiceProvider Services => (_host ?? throw new InvalidOperationException("Silo host is not started.")).Services;

    public string NewBrain(string prefix = "inobrain")
        => $"{prefix}{Interlocked.Increment(ref _brainCounter)}";

    public IDisposable AsPerson(string brain)
        => Edge.Enter(Edge.Person(brain, brain, new BrainId(brain)));

    public EdgeAuthority Edge => Services.GetRequiredService<EdgeAuthority>();

    public IScreen InoScreen(string brain)
        => SiloNeurons.Get<IScreen>(Services, new NeuronId(new BrainId(brain), "app.ino", "default"));

    public IGrainFactory Grains => Services.GetRequiredService<IGrainFactory>();

    // Ino's messages live in its conversation thread (a control.chatThread slot): the screen a person sees is Ino's view with the
    // thread drawn in place of the slot, composed with the slot's props the way the client composes them.
    public NeuronId ThreadOf(string brain)
    {
        var ino = new NeuronId(new BrainId(brain), "app.ino", "default");
        return new NeuronId(ino.Brain, "control.chatThread", ScreenExtensions.ChildAddress(ino, InoView.ConversationKey));
    }

    public IScreen ThreadScreen(string brain) => SiloNeurons.Get<IScreen>(Services, ThreadOf(brain));

    // Activates a node of the conversation thread (the composer, a message's retry, the stop, the error line's retry) on its current view.
    public async Task ActOnThreadAsync(string brain, string key, string? value = null)
    {
        var thread = ThreadScreen(brain);
        for (var attempt = 0; ; attempt++)
        {
            var current = await thread.Read() ?? throw new InvalidOperationException("The conversation thread has no view.");
            try
            {
                await thread.Activate(current.Alias, current.Generation, key, value, brain);
                return;
            }
            catch (ActivationRefusedException refused) when (refused.Reason == ActivationRefusedException.Stale && attempt < 5)
            {
                await Task.Delay(25);
            }
        }
    }

    public async Task<PublishedView?> ReadComposedAsync(IScreen screen)
    {
        var published = await screen.Read();
        if (published is null || ChatThreadComposition.FirstSlot(published.View) is not { } slot) { return published; }
        if (!NeuronId.TryParse(slot.NeuronUri, out var threadId) || await SiloNeurons.Get<IScreen>(Services, threadId).Read() is not { } thread) { return published; }
        var threadView = System.Text.Json.JsonSerializer.Deserialize<View>(thread.View, RuntimeProtocol.OptionsFor(typeof(View)))!;
        var composed = ChatThreadComposition.Compose(Assert.IsType<ChatThreadView>(threadView.Root), ChatThreadComposition.PropsOf(slot.Props));
        var view = System.Text.Json.Nodes.JsonNode.Parse(published.View.GetRawText())!;
        ReplaceSlot(view, System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize<Node>(composed, RuntimeProtocol.OptionsFor(typeof(Node))))!);
        return published with { View = System.Text.Json.JsonSerializer.SerializeToElement(view) };
    }

    private static bool ReplaceSlot(System.Text.Json.Nodes.JsonNode node, System.Text.Json.Nodes.JsonNode replacement)
    {
        switch (node)
        {
            case System.Text.Json.Nodes.JsonObject obj:
                foreach (var (name, value) in obj.ToArray())
                {
                    if (value is System.Text.Json.Nodes.JsonObject child && (string?)child["refKind"] == "slot") { obj[name] = replacement; return true; }
                    if (value is not null && ReplaceSlot(value, replacement)) { return true; }
                }
                return false;
            case System.Text.Json.Nodes.JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is System.Text.Json.Nodes.JsonObject child && (string?)child["refKind"] == "slot") { array[i] = replacement; return true; }
                    if (array[i] is { } item && ReplaceSlot(item, replacement)) { return true; }
                }
                return false;
            default:
                return false;
        }
    }

    public async Task<PublishedView> WaitForScreenAsync(
        IScreen screen,
        Func<PublishedView, bool> predicate,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        PublishedView? last = null;
        while (sw.Elapsed < TimeSpan.FromSeconds(10))
        {
            last = await ReadComposedAsync(screen);
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
        if (view is not null && new PresentedView("main", view).Nodes.OfType<Conversation>().FirstOrDefault() is { } conv)
        {
            conversation = conv;
            return true;
        }

        conversation = null!;
        return false;
    }

    public static Conversation RequireConversation(PublishedView published)
    {
        Assert.True(TryGetConversation(published, out var conversation), "The screen shows no conversation.");
        return conversation;
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
            typeof(DevicesModule).Assembly,
            typeof(IDevice).Assembly,
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
                DevicesModule.ConfigureServices(services, config);
                FlutterModule.ConfigureServices(services, config);
                services.TryAddSingleton(new MachineId(Path.Combine(_home.Path, "machine-id")));
                services.RemoveAll<ILocalRuntime>();
                services.AddSingleton<ILocalRuntime>(LocalRuntime);
                services.AddIntoChatOSConnections();
                services.AddNeuronProxies();
                services.RemoveAll<IBrainObserver>();
                services.AddSingleton<IBrainObserver>(_observer);
                services.RemoveAll<OsNeuronSurface>();
                services.AddSingleton(sp => new OsNeuronSurface(
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
                    typeof(IntoChat.OS.Modules.Runs.RunsModule),
                    typeof(IdentityModule),
                    typeof(AuthorityPlainNeurons),
                    typeof(AIModule),
                    typeof(DevicesModule),
                    typeof(AspireModule),
                    typeof(FlutterModule),
                ],
            },
            CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_host is not null)
            {
                await _host.DisposeAsync();
            }
        }
        finally
        {
            _home.Dispose();
        }
    }

    private sealed class NoOpSecretRedactor : ISecretRedactor
    {
        public ValueTask<string?> Redact(string? text, CancellationToken cancellationToken)
            => ValueTask.FromResult(text);

        public ValueTask<int> TailLength(CancellationToken cancellationToken)
            => ValueTask.FromResult(0);
    }

    private readonly CompiledBrainObserver _observer = new();

    public void ObserveInstance(string type, string key) => _observer.Record(type, key);

    private sealed class CompiledBrainObserver : IBrainObserver
    {
        private readonly ConcurrentQueue<BrainInstance> _recorded = new();
        private static readonly BrainType[] HostedTypes = new CompiledTypes(
        [
            typeof(IMemory).Assembly,
            typeof(IUserSettings).Assembly,
            typeof(IAspire).Assembly,
            typeof(InoWiringTestModule).Assembly,
        ]).Entries;

        public void Record(string type, string key)
            => _recorded.Enqueue(new BrainInstance(type, key, DateTimeOffset.UtcNow, "test"));

        public Task<BrainType[]> Types(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(HostedTypes);
        }

        public IAsyncEnumerable<BrainInstance> Instances<T>(CancellationToken cancellationToken = default) where T : class
        {
            var type = typeof(T).GetCustomAttributes(typeof(DefaultNeuronTypeAttribute), inherit: false)
                .OfType<DefaultNeuronTypeAttribute>()
                .FirstOrDefault()?.Type
                ?? typeof(T).FullName;
            return Instances(type, cancellationToken);
        }

        public IAsyncEnumerable<BrainInstance> Instances(string? type, CancellationToken cancellationToken = default)
            => MatchingInstances(brain: null, type, cancellationToken);

        public IAsyncEnumerable<BrainInstance> InstancesIn(string brain, string? type, CancellationToken cancellationToken = default)
            => MatchingInstances(brain, type, cancellationToken);

        private async IAsyncEnumerable<BrainInstance> MatchingInstances(
            string? brain,
            string? type,
            [EnumeratorCancellation] CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            foreach (var item in _recorded)
            {
                if (type is not null && !string.Equals(item.Type, type, StringComparison.Ordinal))
                {
                    continue;
                }
                if (brain is not null && !item.Key.StartsWith(brain + "/", StringComparison.Ordinal))
                {
                    continue;
                }
                yield return item;
            }
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
            _ = id;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<System.Text.Json.JsonElement?>(null);
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

        public Task<Member?> EnsureMember(Member member, CancellationToken cancellationToken)
            => Task.FromResult<Member?>(member);

        public Task RemoveMember(string brainScopeId, string accountId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RemovePrincipalMember(string brainScopeId, string principalId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<DigitalBrain.Capabilities.Identity.CallDecision?> Authorize(DigitalBrain.Capabilities.Identity.CallRequest request, CancellationToken cancellationToken)
            => Task.FromResult<DigitalBrain.Capabilities.Identity.CallDecision?>(new DigitalBrain.Capabilities.Identity.CallDecision { Allowed = true });

        public Task<IReadOnlyList<DigitalBrain.Capabilities.Identity.Grant>> ListGrants(string brainScopeId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<DigitalBrain.Capabilities.Identity.Grant>>([]);

        public Task<DigitalBrain.Capabilities.Identity.Grant?> GrantAsOwner(DigitalBrain.Capabilities.Identity.Grant grant, CancellationToken cancellationToken)
            => Task.FromResult<DigitalBrain.Capabilities.Identity.Grant?>(grant);

        public Task<bool> RevokeAsOwner(string appId, string semanticTypeId, DigitalBrain.Capabilities.Identity.GrantMode mode, CancellationToken cancellationToken)
            => Task.FromResult(true);
    }
}
