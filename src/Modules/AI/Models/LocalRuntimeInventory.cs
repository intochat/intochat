using System.Collections.Immutable;
using DigitalBrain;
using DigitalBrain.Brains;
using DigitalBrain.Log;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.Devices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.AI.Models;

internal sealed record InstalledLocalModel(ILocalRuntime Runtime, RuntimeStatus Probe, ModelCard Card);

internal sealed record ProviderProbeState(bool AnyReachable, Uri? FirstEndpoint);

internal sealed record LocalRuntimeSnapshot(
    ImmutableArray<ModelCard> Cards,
    ImmutableDictionary<string, InstalledLocalModel> InstalledById,
    ImmutableDictionary<AiProvider, ProviderProbeState> ProviderProbes)
{
    internal static LocalRuntimeSnapshot Empty { get; } = new(
        [.. KnownModels.All],
        ImmutableDictionary<string, InstalledLocalModel>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase),
        ImmutableDictionary<AiProvider, ProviderProbeState>.Empty);

    internal bool TryGetInstalled(string modelId, out InstalledLocalModel installed)
        => InstalledById.TryGetValue(modelId, out installed!);

    internal bool IsProviderReachable(AiProvider provider, out Uri? firstEndpoint)
    {
        if (ProviderProbes.TryGetValue(provider, out var state) && state.AnyReachable)
        {
            firstEndpoint = state.FirstEndpoint;
            return true;
        }

        firstEndpoint = null;
        return false;
    }

    internal bool AnyDeviceChatReachable()
        => ProviderProbes.Any(static entry => LocalRuntimeInventory.IsDeviceChatProvider(entry.Key) && entry.Value.AnyReachable);
}

/// <summary>
/// Probes registered <see cref="ILocalRuntime"/> instances and <see cref="IDeviceChatRoute"/> into
/// an immutable <see cref="LocalRuntimeSnapshot"/> and resolves local runtime ownership for models.
/// </summary>
internal sealed class LocalRuntimeInventory
{
    private readonly IServiceProvider? _services;
    private readonly IEnumerable<ILocalRuntime>? _localRuntimes;
    private readonly IDeviceChatRoute? _deviceRoute;

    internal static readonly AiProvider[] CloudChatProviders =
    [
        AiProvider.OpenAI,
        AiProvider.Anthropic,
        AiProvider.Google,
        AiProvider.XAI,
        AiProvider.OpenRouter,
        AiProvider.AzureFoundry,
    ];

    internal LocalRuntimeInventory(IServiceProvider services, IEnumerable<ILocalRuntime>? localRuntimes = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        _localRuntimes = localRuntimes;
    }

    internal LocalRuntimeInventory(IEnumerable<ILocalRuntime> localRuntimes, IDeviceChatRoute? deviceRoute = null)
    {
        ArgumentNullException.ThrowIfNull(localRuntimes);
        _localRuntimes = localRuntimes;
        _deviceRoute = deviceRoute;
    }

    internal static bool IsCloudChatProvider(AiProvider provider)
        => provider is AiProvider.OpenAI or AiProvider.Anthropic or AiProvider.Google or AiProvider.XAI or AiProvider.OpenRouter or AiProvider.AzureFoundry;

    internal static bool IsDeviceProvider(AiProvider provider)
        => provider is AiProvider.Ollama or AiProvider.FoundryLocal or AiProvider.OpenAICompatible or AiProvider.SherpaOnnx;

    internal static bool IsDeviceChatProvider(AiProvider provider)
        => provider is AiProvider.Ollama or AiProvider.FoundryLocal or AiProvider.OpenAICompatible;

    internal static AiProvider ProviderOf(RuntimeId id)
        => id == RuntimeId.Ollama ? AiProvider.Ollama
            : id == RuntimeId.FoundryLocal ? AiProvider.FoundryLocal
            : string.Equals(id.Value, "sherpa-onnx", StringComparison.OrdinalIgnoreCase) ? AiProvider.SherpaOnnx
            : AiProvider.OpenAICompatible;

    private IDeviceChatRoute? DeviceRoute => _deviceRoute ?? _services?.GetService<IDeviceChatRoute>();

    internal ILocalRuntime[] ActiveRuntimes()
    {
        var registered = _localRuntimes ?? _services?.GetServices<ILocalRuntime>() ?? [];
        return [.. registered
            .OrderBy(static r => r.Id == RuntimeId.Ollama ? 0 : r.Id == RuntimeId.FoundryLocal ? 1 : 2)
            .ThenBy(static r => r.Id.Value, StringComparer.Ordinal)];
    }

    internal async Task<LocalRuntimeSnapshot> ProbeAsync(CancellationToken ct)
    {
        var cards = new List<ModelCard>(KnownModels.All.Count + 8);
        var indexById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in KnownModels.All)
        {
            indexById[card.Id.Value] = cards.Count;
            cards.Add(card);
        }

        var installedById = ImmutableDictionary.CreateBuilder<string, InstalledLocalModel>(StringComparer.OrdinalIgnoreCase);
        var providerProbes = ImmutableDictionary.CreateBuilder<AiProvider, ProviderProbeState>();

        var runtimes = ActiveRuntimes();
        var runtimeStates = runtimes.Length == 0
            ? []
            : await Task.WhenAll(runtimes.Select(runtime => ProbeAndCollectAsync(runtime, ct))).ConfigureAwait(false);

        foreach (var (runtime, probe, installed) in runtimeStates)
        {
            var runtimeProvider = ProviderOf(runtime.Id);

            providerProbes[runtimeProvider] = providerProbes.TryGetValue(runtimeProvider, out var existingProbe)
                ? new ProviderProbeState(
                    existingProbe.AnyReachable || probe.Reachable,
                    existingProbe.FirstEndpoint ?? probe.Endpoint)
                : new ProviderProbeState(probe.Reachable, probe.Endpoint);

            if (!probe.Reachable)
            {
                continue;
            }

            foreach (var localCard in installed)
            {
                var canonical = KnownModels.Find(localCard.Id.Value);
                var lookupId = canonical?.Id.Value ?? localCard.Id.Value;

                ModelCard effectiveCard;
                if (indexById.TryGetValue(lookupId, out var existingIndex))
                {
                    effectiveCard = localCard.Bytes is { } bytes && cards[existingIndex].Bytes != bytes
                        ? cards[existingIndex] with { Bytes = bytes }
                        : cards[existingIndex];
                    cards[existingIndex] = effectiveCard;
                }
                else
                {
                    effectiveCard = localCard;
                    indexById[localCard.Id.Value] = cards.Count;
                    cards.Add(effectiveCard);
                }

                var installedEntry = new InstalledLocalModel(runtime, probe, effectiveCard);
                installedById.TryAdd(lookupId, installedEntry);
                installedById.TryAdd(localCard.Id.Value, installedEntry);
            }
        }

        return new LocalRuntimeSnapshot([.. cards], installedById.ToImmutable(), providerProbes.ToImmutable());
    }

    internal async Task<ModelCard[]> ListWithDeviceRoutesAsync(
        LocalRuntimeSnapshot snapshot,
        BrainId? brain,
        Abilities need,
        CancellationToken ct)
    {
        var cards = new List<ModelCard>(snapshot.Cards.Length + 4);
        cards.AddRange(snapshot.Cards);
        var seen = new HashSet<string>(snapshot.Cards.Select(static c => c.Id.Value), StringComparer.OrdinalIgnoreCase);

        if (brain is { } owner)
        {
            foreach (var deviceCard in await SafeRouteModelsAsync(owner, ct).ConfigureAwait(false))
            {
                if (seen.Add(deviceCard.Id.Value))
                {
                    cards.Add(deviceCard);
                }
            }
        }

        return need == Abilities.None
            ? [.. cards]
            : [.. cards.Where(card => (card.Can & need) == need)];
    }

    internal async Task<ModelCard[]> SafeRouteModelsAsync(BrainId owner, CancellationToken ct)
    {
        if (DeviceRoute is not { } route)
        {
            return [];
        }

        try
        {
            return await route.Models(owner, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            OsConsoleLog.For<LocalRuntimeInventory>().LogSwallowed(error, $"listing routed device models for brain '{owner.Value}'");
            return [];
        }
    }

    internal async Task<ILocalRuntime?> FindRuntimeForModelAsync(AiProvider provider, string modelId, CancellationToken ct)
    {
        var matching = ActiveRuntimes().Where(r => ProviderOf(r.Id) == provider).ToArray();
        if (matching.Length == 0)
        {
            return null;
        }

        if (matching.Length == 1)
        {
            return matching[0];
        }

        foreach (var candidate in matching)
        {
            var installed = await CollectInstalledSafeAsync(candidate, ct).ConfigureAwait(false);
            if (installed.Any(c => string.Equals(c.Id.Value, modelId, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }

        return matching[0];
    }

    internal async Task<ILocalRuntime?> FindInstalledRuntimeAsync(ModelId id, ILogger? logger, CancellationToken ct)
    {
        foreach (var runtime in ActiveRuntimes())
        {
            try
            {
                await foreach (var installed in runtime.Installed(ct).ConfigureAwait(false))
                {
                    var canonical = KnownModels.Find(installed.Id.Value);
                    if (string.Equals(installed.Id.Value, id.Value, StringComparison.OrdinalIgnoreCase)
                        || (canonical is not null && canonical.Id == id))
                    {
                        return runtime;
                    }
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                (logger ?? OsConsoleLog.For<LocalRuntimeInventory>())
                    .LogSwallowed(error, $"checking installed models on runtime '{runtime.Id.Value}' for '{id.Value}'");
            }
        }

        return null;
    }

    internal async Task<(IChatClient? Client, string? UnavailableReason)> TryCreateDeviceRouteClientAsync(
        string? brainId,
        ResolvedModel resolved,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        if (!DeviceModelRef.TryParse(resolved.Model, out var deviceRef))
        {
            return (null, "invalid-device-id");
        }

        if (string.IsNullOrWhiteSpace(brainId) || !BrainScope.IsValidId(brainId))
        {
            return (null, "no-brain");
        }

        if (DeviceRoute is not { } route)
        {
            return (null, "no-route");
        }

        var client = await route.Chat(new BrainId(brainId), deviceRef, ct).ConfigureAwait(false);
        return client is not null ? (client, null) : (null, "offline");
    }

    private static async Task<(ILocalRuntime Runtime, RuntimeStatus Probe, List<ModelCard> Installed)> ProbeAndCollectAsync(
        ILocalRuntime runtime,
        CancellationToken ct)
    {
        var probe = await SafeProbeAsync(runtime, ct).ConfigureAwait(false);
        if (!probe.Reachable)
        {
            return (runtime, probe, []);
        }

        var installed = await CollectInstalledSafeAsync(runtime, ct).ConfigureAwait(false);
        return (runtime, probe, installed);
    }

    private static async Task<RuntimeStatus> SafeProbeAsync(ILocalRuntime runtime, CancellationToken ct)
    {
        try
        {
            return await runtime.Probe(ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            OsConsoleLog.For<LocalRuntimeInventory>().LogSwallowed(error, $"probing local runtime '{runtime.Id.Value}'");
            return new RuntimeStatus(runtime.Id, Reachable: false, Version: null, Endpoint: null);
        }
    }

    private static async Task<List<ModelCard>> CollectInstalledSafeAsync(ILocalRuntime runtime, CancellationToken ct)
    {
        var list = new List<ModelCard>();
        try
        {
            await foreach (var card in runtime.Installed(ct).ConfigureAwait(false))
            {
                list.Add(card);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            OsConsoleLog.For<LocalRuntimeInventory>().LogSwallowed(error, $"listing installed models on runtime '{runtime.Id.Value}'");
        }
        return list;
    }
}
