using System.Runtime.CompilerServices;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Kernel;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using IntoChat.OS.Host.Kernel;
using IntoChat.OS.Host.Surface;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts;
using IntoChat.OS.Modules.Postgres.Tables;
using IntoChat.OS.Modules.Web;
using IntoChat.OS.Modules.Web.Search;
using IntoChat.OS.Tests.Support.Fakes.AI;
using IntoChat.OS.Tests.Support.Fakes.Postgres;
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
                services.RemoveAll<BrainKeyAvailability>();
                services.RemoveAll<IBrainObserver>();
                services.AddSingleton<IBrainObserver, CompiledBrainObserver>();
                services.RemoveAll<NeuronTypeSearch>();
                services.AddSingleton(sp => new NeuronTypeSearch(
                    sp.GetRequiredService<IBrainObserver>(),
                    [],
                    embeddings: null,
                    callers: sp.GetService<ICallerAmbient>()));
                services.RemoveAll<OsNeuronSurface>();
                services.AddSingleton(sp => new OsNeuronSurface(
                    sp.GetRequiredService<INeuronProxies>(),
                    sp.GetRequiredService<IBrainObserver>(),
                    sp.GetRequiredService<NeuronTypeSearch>(),
                    plain: sp));
            })
            .Add<LlmScript, LlmScript>(script)
            .Add<IChatClient, ScriptedChatClient>(client)
            .Add<IAiCredentials, NoConfiguredProviders>(new())
            .Add<ISecretRedactor, NoOpSecretRedactor>(new());
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
        [
            typeof(IWebSearch).Assembly,
            typeof(IMemory).Assembly,
            typeof(ITable).Assembly,
            typeof(IApp).Assembly,
            typeof(IFriends).Assembly,
            typeof(IConversation).Assembly,
        ]).Entries;

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
