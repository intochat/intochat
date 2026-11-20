using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities.Compute;
using DigitalBrain.Neurons;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models.Ollama;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Runtime;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.AI.Models;

// The one resolver from a role or model key to a client. Precedence:
// turn override -> person role override (IUserSettings) -> deployment RoleMap -> first available card with the role's ability.
public sealed class ModelCatalog
{
    private readonly IServiceProvider services;
    private readonly IOptionsMonitor<AIOptions> options;
    private readonly IEnumerable<ILocalCardSource>? localSources;

    public ModelCatalog(IServiceProvider services, IOptionsMonitor<AIOptions> options)
        : this(services, options, localSources: null)
    {
    }

    internal ModelCatalog(IServiceProvider services, IOptionsMonitor<AIOptions> options, IEnumerable<ILocalCardSource>? localSources)
    {
        this.services = services;
        this.options = options;
        this.localSources = localSources;
    }
    internal const string NoKeyReason = "Add a key in API keys";
    internal const string NotInstalledReason = "Not installed";
    internal const string NotConfiguredReason = "Not configured";

    private IAiCredentials Deployment => services.GetRequiredService<IAiCredentials>();

    private string? Topology => services.GetService<IConfiguration>()?[IntoChatOSNames.TopologyKey];

    private IAiCredentials CredentialsFor(string? brainId)
        => brainId is not null && BrainScope.IsValidId(brainId) && services.GetService<BrainKeyAvailability>() is { } keys
            ? new BrainAwareCredentials(Deployment, keys, brainId)
            : Deployment;

    private IEnumerable<ILocalCardSource> ActiveLocalSources()
    {
        var registered = localSources ?? services.GetServices<ILocalCardSource>();
        var list = registered as ICollection<ILocalCardSource> ?? [.. registered];
        if (list.Count > 0)
        {
            return list;
        }

        return services.GetService<ILocalOllamaProbe>() is { } probe
            ? [new OllamaLocalCardSource(probe)]
            : [];
    }

    private bool HasLocalProbe => services.GetService<ILocalOllamaProbe>() is not null;

    internal bool HasProfile(string name) => options.CurrentValue.ModelProfiles.ContainsKey(name);

    private bool HasHostChatClient => services.GetService<IChatClient>() is not null and not RebuildingChatClient;

    internal Task<LocalOllamaDetection?> DetectLocal(CancellationToken ct)
        => services.GetService<ILocalOllamaProbe>() is { } probe
            ? probe.Detect(ct)
            : Task.FromResult<LocalOllamaDetection?>(null);

    /// <summary>Applies the shared local-topology Ollama endpoint and optional local model override.</summary>
    internal static void ApplyTopologyDefaults(AIOptions ai, IConfiguration? configuration)
    {
        ArgumentNullException.ThrowIfNull(ai);
        if (configuration is null)
        {
            return;
        }

        var rawTopology = configuration[IntoChatOSNames.TopologyKey];
        if (string.IsNullOrWhiteSpace(rawTopology))
        {
            return;
        }

        var topology = IntoChatOSNames.CanonicalTopology(rawTopology);
        if (topology is not (IntoChatOSNames.TopologyLocal or IntoChatOSNames.TopologyLocalCloud))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(ai.Ollama.Endpoint))
        {
            ai.Ollama.Endpoint = IntoChatOSNames.LocalOllamaEndpoint;
        }

        var localModel = Clean(configuration[IntoChatOSNames.LocalModelKey]);
        if (localModel is not null && !ai.Roles.Device.ContainsKey(Role.Chat))
        {
            ai.Roles.Device[Role.Chat] = KnownModels.Find(localModel)?.Id.Value ?? localModel;
        }
    }

    public Task<ResolvedModel> Resolve(ModelSelection? selection, bool requiresTools, CancellationToken ct)
        => Resolve(null, selection, requiresTools, ct);

    public Task<ResolvedModel> Resolve(string? brainId, ModelSelection? selection, bool requiresTools, CancellationToken ct)
        => Decide(brainId, Role.Chat, selection, requiresTools, ct);

    /// <summary>Single model decision entry point: turn -> person -> deployment -> first available card with the role's ability.</summary>
    public Task<ResolvedModel> Decide(
        string? brainId,
        Role role,
        ModelSelection? turn = null,
        bool requiresTools = false,
        CancellationToken ct = default)
        => DecideCore(brainId, role, turn, requiresTools, personSettingsReader: null, ct);

    internal Task<ResolvedModel> Decide(
        BrainId? brain,
        Role role,
        ModelSelection? turn,
        bool requiresTools,
        Func<CancellationToken, Task<UserSettingsState?>>? personSettingsReader,
        CancellationToken ct)
        => DecideCore(brain?.Value, role, turn, requiresTools, personSettingsReader, ct);

    public async Task<ModelCard[]> List(Abilities need = Abilities.None, CancellationToken ct = default)
    {
        var (cards, _) = await GatherCardsAsync(ct).ConfigureAwait(false);
        return need == Abilities.None
            ? cards
            : [.. cards.Where(card => (card.Can & need) == need)];
    }

    internal async Task<ModelId> Pick(
        BrainId? brain,
        Role role,
        ModelId? turn,
        Func<CancellationToken, Task<UserSettingsState?>>? personSettingsReader,
        CancellationToken ct)
    {
        var selection = turn is { Value.Length: > 0 } explicitTurn ? new ModelSelection(explicitTurn.Value) : null;
        var resolved = await DecideCore(
            brain?.Value,
            role,
            selection,
            requiresTools: RoleNeeds.Of(role).HasFlag(Abilities.Tools),
            personSettingsReader,
            ct).ConfigureAwait(false);

        if (resolved.Provider == AiProvider.Host && string.Equals(resolved.Model, "default", StringComparison.Ordinal))
        {
            throw new NoProviderConfiguredException($"No available model is configured for role '{role}'.");
        }

        return new ModelId(resolved.Model);
    }

    internal async Task<RoleMap> Roles(
        BrainId? brain,
        Func<CancellationToken, Task<UserSettingsState?>>? personSettingsReader,
        CancellationToken ct)
    {
        var baseMap = RoleMaps.For(options.CurrentValue);
        var person = await ReadPersonSettingsAsync(brain?.Value, personSettingsReader, ct).ConfigureAwait(false);
        if (person is null)
        {
            return baseMap;
        }

        var device = baseMap.Device.ToBuilder();
        var cloud = baseMap.Cloud.ToBuilder();
        var changed = false;

        foreach (var role in Enum.GetValues<Role>())
        {
            if (person.RoleOf(role) is { Value.Length: > 0 } chosen)
            {
                device[role] = chosen;
                cloud[role] = chosen;
                changed = true;
            }
        }

        return changed ? new RoleMap(device.ToImmutable(), cloud.ToImmutable()) : baseMap;
    }

    internal async Task<ModelAvailability[]> Availability(BrainId? brain, CancellationToken ct)
    {
        var configuration = options.CurrentValue;
        var credentials = CredentialsFor(brain?.Value);
        var (cards, installedLocalIds) = await GatherCardsAsync(ct).ConfigureAwait(false);
        var hasProbe = HasLocalProbe;

        var results = new ModelAvailability[cards.Length];
        for (var i = 0; i < cards.Length; i++)
        {
            var card = cards[i];
            var (ready, reason) = await CheckCardAvailabilityAsync(card, configuration, credentials, installedLocalIds, hasProbe, ct).ConfigureAwait(false);
            results[i] = new ModelAvailability(card.Id, ready, reason);
        }

        return results;
    }

    private async Task<ResolvedModel> DecideCore(
        string? brainId,
        Role role,
        ModelSelection? turn,
        bool requiresTools,
        Func<CancellationToken, Task<UserSettingsState?>>? personSettingsReader,
        CancellationToken ct)
    {
        var credentials = CredentialsFor(brainId);
        turn ??= new();
        var configuration = options.CurrentValue;

        var turnKey = Clean(turn.Model);
        string? personKey = null;
        if (turnKey is null)
        {
            var personSettings = await ReadPersonSettingsAsync(brainId, personSettingsReader, ct).ConfigureAwait(false);
            personKey = Clean(personSettings?.RoleOf(role)?.Value);
        }

        var inheritDeploymentTuning = turnKey is null && personKey is null;
        var reasoning = Clean(turn.Reasoning) ?? (inheritDeploymentTuning ? Clean(configuration.Reasoning) : null);
        var maxTokens = turn.MaxOutputTokens ?? (inheritDeploymentTuning ? configuration.MaxOutputTokens : null);

        if (turnKey is not null || personKey is not null)
        {
            var explicitKey = turnKey ?? personKey!;
            return await ResolveExplicitAsync(
                explicitKey,
                turn,
                configuration,
                credentials,
                reasoning,
                maxTokens,
                requiresTools,
                paramName: turnKey is not null ? nameof(turn) : "person",
                ct).ConfigureAwait(false);
        }

        var (allCards, installedLocalIds) = await GatherCardsAsync(ct).ConfigureAwait(false);
        var cardsById = new Dictionary<string, ModelCard>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in allCards)
        {
            cardsById.TryAdd(card.Id.Value, card);
        }

        var hasProbe = HasLocalProbe;
        var needed = RoleNeeds.Of(role) | (requiresTools ? Abilities.Tools : Abilities.None);
        var profileNames = configuration.ModelProfiles.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 1. Explicit deployment role configuration for the active topology.
        string? firstExplicitDeployment = null;
        foreach (var candidateId in RoleMaps.Candidates(configuration.Roles.ToRoleMap(), Topology, role))
        {
            firstExplicitDeployment ??= candidateId.Value;

            if (StoredModel.TryParse(candidateId.Value, profileNames, out var kind, out var parsedValue))
            {
                if (kind == StoredModelKind.Profile && TryFindProfile(configuration, parsedValue, out var matchedName, out var profile))
                {
                    var profileProvider = ParseProvider(Clean(profile.Provider) ?? throw new ArgumentException($"Model profile '{matchedName}' requires a provider."));
                    var profileEndpoint = Clean(profile.Endpoint);
                    if (await IsProviderReadyForExplicitAsync(profileProvider, profileEndpoint, configuration, credentials, ct).ConfigureAwait(false))
                    {
                        return await ResolveExplicitAsync(candidateId.Value, turn, configuration, credentials, reasoning, maxTokens, requiresTools, nameof(role), ct).ConfigureAwait(false);
                    }
                    continue;
                }

                if (kind == StoredModelKind.Card && cardsById.TryGetValue(parsedValue, out var explicitCard))
                {
                    var (ready, _) = await CheckCardAvailabilityAsync(explicitCard, configuration, credentials, installedLocalIds, hasProbe, ct).ConfigureAwait(false);
                    if (ready)
                    {
                        return await ResolveCardAsync(explicitCard, configuration, credentials, reasoning, maxTokens, requiresTools, ct).ConfigureAwait(false);
                    }
                    continue;
                }

                if (kind == StoredModelKind.OllamaTag)
                {
                    var tagCard = cardsById.TryGetValue(parsedValue, out var found)
                        ? found
                        : MakeDynamicOllamaCard(parsedValue);
                    var (ready, _) = await CheckCardAvailabilityAsync(tagCard, configuration, credentials, installedLocalIds, hasProbe, ct).ConfigureAwait(false);
                    if (ready)
                    {
                        return await ResolveCardAsync(tagCard, configuration, credentials, reasoning, maxTokens, requiresTools, ct).ConfigureAwait(false);
                    }
                    continue;
                }
            }
            else if (cardsById.TryGetValue(candidateId.Value, out var localCandidate))
            {
                var (ready, _) = await CheckCardAvailabilityAsync(localCandidate, configuration, credentials, installedLocalIds, hasProbe, ct).ConfigureAwait(false);
                if (ready)
                {
                    return await ResolveCardAsync(localCandidate, configuration, credentials, reasoning, maxTokens, requiresTools, ct).ConfigureAwait(false);
                }
            }
        }

        // 2. Stock deployment defaults (KnownModels.Defaults) for the active topology.
        foreach (var defaultId in RoleMaps.Candidates(KnownModels.Defaults, Topology, role))
        {
            if (cardsById.TryGetValue(defaultId.Value, out var defaultCard)
                && (defaultCard.Can & needed) == needed)
            {
                var (ready, _) = await CheckCardAvailabilityAsync(defaultCard, configuration, credentials, installedLocalIds, hasProbe, ct).ConfigureAwait(false);
                if (ready)
                {
                    return await ResolveCardAsync(defaultCard, configuration, credentials, reasoning, maxTokens, requiresTools, ct).ConfigureAwait(false);
                }
            }
        }

        // 3. First available card declaring the role's required abilities.
        foreach (var card in allCards)
        {
            if ((card.Can & needed) != needed)
            {
                continue;
            }

            var (ready, _) = await CheckCardAvailabilityAsync(card, configuration, credentials, installedLocalIds, hasProbe, ct).ConfigureAwait(false);
            if (ready)
            {
                return await ResolveCardAsync(card, configuration, credentials, reasoning, maxTokens, requiresTools, ct).ConfigureAwait(false);
            }
        }

        // 4. Fallback when no card is available: Host client (in tests) or fail on the explicit deployment choice.
        if (firstExplicitDeployment is not null)
        {
            return await ResolveExplicitAsync(firstExplicitDeployment, turn, configuration, credentials, reasoning, maxTokens, requiresTools, nameof(role), ct).ConfigureAwait(false);
        }

        var host = new ResolvedModel(AiProvider.Host, "default", null, null, string.Empty, Abilities.Text | Abilities.Tools, NormalizeReasoning(reasoning), maxTokens);
        return host with { Revision = Revision(host) };
    }

    private async Task<ResolvedModel> ResolveExplicitAsync(
        string key,
        ModelSelection turn,
        AIOptions configuration,
        IAiCredentials credentials,
        string? reasoning,
        int? maxTokens,
        bool requiresTools,
        string paramName,
        CancellationToken ct)
    {
        var profileNames = configuration.ModelProfiles.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        AiProvider provider;
        string modelId;
        string? endpoint = null;
        string? profileName = null;
        Abilities? declared = null;
        ModelCard? preset = null;

        if (StoredModel.TryParse(key, profileNames, out var kind, out var parsedValue))
        {
            switch (kind)
            {
                case StoredModelKind.Profile when TryFindProfile(configuration, parsedValue, out var matchedProfileName, out var profile):
                    profileName = matchedProfileName;
                    provider = ParseProvider(Clean(profile.Provider) ?? throw new ArgumentException($"Model profile '{matchedProfileName}' requires a provider."));
                    modelId = Clean(profile.Model) ?? throw new ArgumentException($"Model profile '{matchedProfileName}' requires a model ID.");
                    endpoint = Clean(profile.Endpoint);
                    reasoning = Clean(turn.Reasoning) ?? Clean(profile.Reasoning);
                    maxTokens = turn.MaxOutputTokens ?? profile.MaxOutputTokens;
                    declared = profile.Capabilities;
                    preset = KnownModels.Find(modelId);
                    if (preset is not null && preset.Provider != provider) { preset = null; }
                    break;
                case StoredModelKind.Card:
                    preset = KnownModels.Find(parsedValue)!;
                    provider = preset.Provider;
                    modelId = preset.Id.Value;
                    break;
                case StoredModelKind.OllamaTag:
                    provider = AiProvider.Ollama;
                    modelId = parsedValue;
                    preset = KnownModels.Find(modelId);
                    if (preset is not null && preset.Provider != AiProvider.Ollama) { preset = null; }
                    declared = preset?.Can ?? (Abilities.Text | Abilities.Tools);
                    break;
                default:
                    throw new ArgumentException($"Unknown model '{key}'. Use a catalogued model id or a configured profile name.", paramName);
            }
        }
        else
        {
            var (allCards, _) = await GatherCardsAsync(ct).ConfigureAwait(false);
            var localCard = allCards.FirstOrDefault(c => string.Equals(c.Id.Value, key, StringComparison.OrdinalIgnoreCase));
            if (localCard is null)
            {
                throw new ArgumentException($"Unknown model '{key}'. Use a catalogued model id or a configured profile name.", paramName);
            }

            preset = localCard;
            provider = localCard.Provider;
            modelId = localCard.Id.Value;
            declared = localCard.Can;
        }

        return await FinalizeResolutionAsync(
            provider,
            modelId,
            endpoint,
            profileName,
            declared,
            preset,
            configuration,
            credentials,
            reasoning,
            maxTokens,
            requiresTools,
            ct).ConfigureAwait(false);
    }

    private Task<ResolvedModel> ResolveCardAsync(
        ModelCard card,
        AIOptions configuration,
        IAiCredentials credentials,
        string? reasoning,
        int? maxTokens,
        bool requiresTools,
        CancellationToken ct)
        => FinalizeResolutionAsync(
            card.Provider,
            card.Id.Value,
            endpoint: null,
            profileName: null,
            declared: card.Can,
            preset: card,
            configuration,
            credentials,
            reasoning,
            maxTokens,
            requiresTools,
            ct);

    private async Task<ResolvedModel> FinalizeResolutionAsync(
        AiProvider provider,
        string modelId,
        string? endpoint,
        string? profileName,
        Abilities? declared,
        ModelCard? preset,
        AIOptions configuration,
        IAiCredentials credentials,
        string? reasoning,
        int? maxTokens,
        bool requiresTools,
        CancellationToken ct)
    {
        if (provider == AiProvider.Ollama && preset is not null)
        {
            modelId = OllamaProviderFactory.TryResolveConfiguredTag(configuration.Ollama, preset) ?? modelId;
        }

        if (modelId.Length > 200 || modelId.Any(char.IsControl))
        {
            throw new ArgumentException("Model ID must contain at most 200 printable characters.", nameof(modelId));
        }

        var isChatFactoryProvider = provider is AiProvider.OpenAI or AiProvider.Anthropic or AiProvider.Google
            or AiProvider.XAI or AiProvider.OpenRouter or AiProvider.Ollama;

        if (isChatFactoryProvider)
        {
            var factory = AIClients.Factory(provider);
            if (!await factory.IsConfigured(configuration, credentials, ct).ConfigureAwait(false)
                && !(provider == AiProvider.Ollama && endpoint is not null))
            {
                if (HasHostChatClient && !await AnyProviderConfigured(configuration, credentials, ct).ConfigureAwait(false))
                {
                    var host = new ResolvedModel(AiProvider.Host, modelId, null, profileName, string.Empty,
                        declared ?? preset?.Can ?? (Abilities.Text | Abilities.Tools), NormalizeReasoning(reasoning), maxTokens);
                    return host with { Revision = Revision(host) };
                }

                if (provider == AiProvider.Ollama)
                {
                    if (await DetectLocal(ct).ConfigureAwait(false) is { } localDetected)
                    {
                        endpoint = localDetected.Endpoint;
                        declared ??= Abilities.Text | Abilities.Tools;
                    }
                    else
                    {
                        throw new NoProviderConfiguredException($"Provider 'Ollama' is not configured. Set {AIKeys.OllamaEndpoint}.");
                    }
                }
                else
                {
                    await credentials.RequireReady(AiIntegrations.IdOf(provider), ct).ConfigureAwait(false);
                }
            }

            endpoint ??= provider == AiProvider.Ollama
                ? Clean(configuration.Ollama.Endpoint)
                : Clean(configuration.Provider(provider).Endpoint);
            if (endpoint is not null || provider == AiProvider.Ollama)
            {
                endpoint = Endpoint(endpoint, provider);
            }
        }
        else if (provider == AiProvider.TypeSafe)
        {
            if (!await credentials.IsReady(AiIntegrations.TypeSafe.Id, ct).ConfigureAwait(false))
            {
                await credentials.RequireReady(AiIntegrations.TypeSafe.Id, ct).ConfigureAwait(false);
            }
            endpoint ??= Clean(configuration.SystemOne.Endpoint);
        }

        var capabilities = declared ?? preset?.Can ?? Abilities.None;
        const Abilities allKnownAbilities = Abilities.Text | Abilities.Tools | Abilities.Vision | Abilities.Reasoning
            | Abilities.StructuredOutput | Abilities.Hear | Abilities.Speak | Abilities.Talk | Abilities.Embed | Abilities.Image;
        if ((capabilities & ~allKnownAbilities) != 0)
        {
            throw new ArgumentException("Model capabilities contain unknown flags.");
        }
        if (requiresTools && !capabilities.HasFlag(Abilities.Tools))
        {
            throw new ArgumentException($"Model '{modelId}' does not declare tool support. Configure Tools capabilities in its model profile before attaching tools.");
        }

        reasoning = NormalizeReasoning(reasoning);
        if (requiresTools && provider == AiProvider.OpenAI && modelId.StartsWith("gpt-5.6", StringComparison.OrdinalIgnoreCase))
        {
            if (reasoning is not null and not "none")
            {
                throw new ArgumentException("GPT-5.6 tools through OpenAI Chat Completions require reasoning 'none'.");
            }
            reasoning = "none";
        }

        if (maxTokens is < 1 or > 1_000_000)
        {
            throw new ArgumentException("MaxOutputTokens must be between 1 and 1000000.");
        }

        var resolved = new ResolvedModel(provider, modelId, endpoint, profileName, string.Empty, capabilities, reasoning, maxTokens);
        return resolved with { Revision = Revision(resolved) };
    }

    private async Task<(ModelCard[] Cards, HashSet<string> InstalledLocalIds)> GatherCardsAsync(CancellationToken ct)
    {
        var cards = new List<ModelCard>(KnownModels.All.Count + 8);
        var indexById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var card in KnownModels.All)
        {
            indexById[card.Id.Value] = cards.Count;
            cards.Add(card);
        }

        var installedLocalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in ActiveLocalSources())
        {
            await foreach (var localCard in source.Installed(ct).ConfigureAwait(false))
            {
                var canonical = KnownModels.Find(localCard.Id.Value);
                var lookupId = canonical?.Id.Value ?? localCard.Id.Value;
                installedLocalIds.Add(lookupId);
                installedLocalIds.Add(localCard.Id.Value);

                if (indexById.TryGetValue(lookupId, out var existingIndex))
                {
                    if (localCard.Bytes is { } bytes && cards[existingIndex].Bytes != bytes)
                    {
                        cards[existingIndex] = cards[existingIndex] with { Bytes = bytes };
                    }
                }
                else
                {
                    indexById[localCard.Id.Value] = cards.Count;
                    cards.Add(localCard);
                }
            }
        }

        return ([.. cards], installedLocalIds);
    }

    private async Task<(bool Ready, string? Reason)> CheckCardAvailabilityAsync(
        ModelCard card,
        AIOptions configuration,
        IAiCredentials credentials,
        HashSet<string> installedLocalIds,
        bool hasLocalProbe,
        CancellationToken ct)
    {
        switch (card.Provider)
        {
            case AiProvider.Ollama:
                if (installedLocalIds.Contains(card.Id.Value))
                {
                    return (true, null);
                }

                if (installedLocalIds.Count > 0)
                {
                    return (false, NotInstalledReason);
                }

                if (!string.IsNullOrWhiteSpace(configuration.Ollama.Endpoint)
                    && (!IsDefaultOrLoopbackOllamaEndpoint(configuration.Ollama.Endpoint)
                        || !hasLocalProbe
                        || configuration.Ollama.Models.Keys.Any(k => string.Equals(k, card.Id.Value, StringComparison.OrdinalIgnoreCase)
                            || KnownModels.Find(k)?.Id == card.Id)))
                {
                    return (true, null);
                }

                return (false, NotInstalledReason);

            case AiProvider.OpenAI:
            case AiProvider.Anthropic:
            case AiProvider.Google:
            case AiProvider.XAI:
            case AiProvider.OpenRouter:
                return await AIClients.Factory(card.Provider).IsConfigured(configuration, credentials, ct).ConfigureAwait(false)
                    ? (true, null)
                    : (false, NoKeyReason);

            case AiProvider.TypeSafe:
                return await credentials.IsReady(AiIntegrations.TypeSafe.Id, ct).ConfigureAwait(false)
                    ? (true, null)
                    : (false, NoKeyReason);

            case AiProvider.FoundryLocal:
                return installedLocalIds.Contains(card.Id.Value)
                    ? (true, null)
                    : (false, NotInstalledReason);

            case AiProvider.OpenAICompatible:
                if (installedLocalIds.Contains(card.Id.Value))
                {
                    return (true, null);
                }
                return configuration.ModelProfiles.Values.Any(p =>
                    string.Equals(p.Provider, nameof(AiProvider.OpenAICompatible), StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(p.Endpoint))
                    ? (true, null)
                    : (false, NotConfiguredReason);

            case AiProvider.Host:
                return HasHostChatClient ? (true, null) : (false, NotConfiguredReason);

            default:
                return (false, NotConfiguredReason);
        }
    }

    private static async Task<bool> IsProviderReadyForExplicitAsync(
        AiProvider provider,
        string? explicitEndpoint,
        AIOptions configuration,
        IAiCredentials credentials,
        CancellationToken ct)
    {
        if (provider == AiProvider.Ollama && !string.IsNullOrWhiteSpace(explicitEndpoint))
        {
            return true;
        }

        if (provider is AiProvider.OpenAI or AiProvider.Anthropic or AiProvider.Google
            or AiProvider.XAI or AiProvider.OpenRouter or AiProvider.Ollama)
        {
            return await AIClients.Factory(provider).IsConfigured(configuration, credentials, ct).ConfigureAwait(false);
        }

        return false;
    }

    private async Task<UserSettingsState?> ReadPersonSettingsAsync(
        string? brainId,
        Func<CancellationToken, Task<UserSettingsState?>>? overrideReader,
        CancellationToken ct)
    {
        if (overrideReader is not null)
        {
            return await overrideReader(ct).ConfigureAwait(false);
        }

        if (brainId is null || !BrainScope.IsValidId(brainId))
        {
            return null;
        }

        if (services.GetService<INeuronProxies>() is { } proxies)
        {
            try
            {
                return await proxies.Get<IUserSettings>(IUserSettings.Key(new BrainId(brainId))).Read().ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                services.GetService<ILoggerFactory>()?.CreateLogger<ModelCatalog>()
                    ?.LogDebug(error, "User settings for brain '{BrainId}' are unavailable; skipping person role override.", brainId);
            }
        }

        return null;
    }

    private static ModelCard MakeDynamicOllamaCard(string tag)
    {
        var colon = tag.IndexOf(':');
        var family = colon > 0 ? tag[..colon] : tag;
        return new ModelCard(new ModelId(tag), AiProvider.Ollama, family, tag, Where.Device, Abilities.Text | Abilities.Tools, Tier.Balanced);
    }

    private static bool TryFindProfile(AIOptions configuration, string key, out string profileName, out AIModelProfileOptions profile)
    {
        if (configuration.ModelProfiles.TryGetValue(key, out var exact))
        {
            profileName = key;
            profile = exact;
            return true;
        }
        foreach (var entry in configuration.ModelProfiles)
        {
            if (string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                profileName = entry.Key;
                profile = entry.Value;
                return true;
            }
        }
        profileName = string.Empty;
        profile = null!;
        return false;
    }

    private static readonly AiProvider[] ExternalChatProviders =
    [
        AiProvider.OpenAI,
        AiProvider.Anthropic,
        AiProvider.Google,
        AiProvider.XAI,
        AiProvider.OpenRouter,
        AiProvider.Ollama,
    ];

    private static async Task<bool> AnyProviderConfigured(AIOptions configuration, IAiCredentials credentials, CancellationToken ct)
    {
        if (configuration.ModelProfiles.Values.Any(profile => !string.IsNullOrWhiteSpace(profile.Endpoint))) { return true; }
        foreach (var provider in ExternalChatProviders)
        {
            if (await AIClients.Factory(provider).IsConfigured(configuration, credentials, ct).ConfigureAwait(false)) { return true; }
        }
        return false;
    }

    private static bool IsDefaultOrLoopbackOllamaEndpoint(string? endpoint)
        => string.IsNullOrWhiteSpace(endpoint)
            || (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.IsLoopback && uri.Port == 11434);

    public Task<IChatClient> CreateClient(ResolvedModel resolved, CancellationToken ct, bool invokesTools = false)
        => CreateClient(null, resolved, ct, invokesTools);

    public async Task<IChatClient> CreateClient(string? brainId, ResolvedModel resolved, CancellationToken ct, bool invokesTools = false)
    {
        var credentials = CredentialsFor(brainId);
        ArgumentNullException.ThrowIfNull(resolved);
        if (!string.Equals(resolved.Revision, Revision(resolved), StringComparison.Ordinal))
        { throw new ArgumentException("The resolved model configuration does not match its revision.", nameof(resolved)); }
        _ = services.GetService<ProviderExecutionGate>()?.Bound(new ChatOptions { MaxOutputTokens = resolved.MaxOutputTokens });
        if (resolved.Provider == AiProvider.Host)
        {
            var hostClient = services.GetService<IChatClient>()
                ?? throw new NoProviderConfiguredException("No AI provider is configured.");
            return new NonDisposing(hostClient);
        }
        var configuration = new AIOptions();
        configuration.Provider(resolved.Provider).Endpoint = resolved.Endpoint;
        var inner = new RebuildingChatClient(
            token => Build(brainId, resolved, configuration, token),
            token => AiIntegrations.KeyedProviders.Contains(resolved.Provider) ? credentials.GenerationOf(AiIntegrations.IdOf(resolved.Provider), token) : Task.FromResult(0L));
        var pinned = new ChatClientBuilder(inner).ConfigureOptions(request =>
        {
            request.ModelId = resolved.Model;
            if (resolved.Reasoning is not null) { request.Reasoning = Reasoning(resolved.Reasoning); }
            if (resolved.MaxOutputTokens is not null) { request.MaxOutputTokens = resolved.MaxOutputTokens; }
        }).Build();
        ct.ThrowIfCancellationRequested();
        return AIClients.BuildChatPipeline(services, !invokesTools || resolved.Capabilities.HasFlag(Abilities.Tools),
            resolved.Provider.ToString(), pinned, resolved.Provider.ToString(), resolved.Model,
            rejectUnsupportedTools: invokesTools, useFunctionInvocation: invokesTools);
    }

    private async Task<IChatClient> Build(string? brainId, ResolvedModel resolved, AIOptions configuration, CancellationToken ct)
    {
        var factory = AIClients.Factory(resolved.Provider);
        if (brainId is not null && BrainScope.IsValidId(brainId) && AiIntegrations.KeyedProviders.Contains(resolved.Provider)
            && !HasProfileEndpoint(resolved)
            && await Deployment.BrainKey(brainId, resolved.Provider, ct).ConfigureAwait(false) is { Length: > 0 } key)
        {
            var publicEndpoint = services.GetRequiredService<ProviderPublicEndpoints>().Of(resolved.Provider);
            return await factory.CreateChatClient(resolved.Model, new AIOptions(), new BrainKeyCredentials(AiIntegrations.IdOf(resolved.Provider), key), publicEndpoint, ct).ConfigureAwait(false);
        }
        return await factory.CreateChatClient(resolved.Model, configuration, Deployment, resolved.Endpoint, ct).ConfigureAwait(false);
    }

    private bool HasProfileEndpoint(ResolvedModel resolved)
        => resolved.Profile is not null
            && Clean(options.CurrentValue.ModelProfiles.GetValueOrDefault(resolved.Profile)?.Endpoint) is not null;

    public ModelDescriptor Describe(ResolvedModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var profile = model.Profile is not null ? options.CurrentValue.ModelProfiles.GetValueOrDefault(model.Profile) : null;
        if (profile?.ContextWindowTokens is <= 0 || profile?.MaximumOutputTokens is <= 0 || profile?.MaximumOutputTokens > profile?.ContextWindowTokens)
        { throw new ArgumentException("Configured model token limits must be positive and output cannot exceed context."); }
        if (profile?.AllowedReasoning?.Any(value => value is not ("none" or "low" or "medium" or "high" or "xhigh")) == true)
        { throw new ArgumentException("AllowedReasoning contains an unsupported reasoning value."); }
        var declared = profile?.Capabilities;
        CapabilitySupport Support(Abilities capability) => model.Capabilities.HasFlag(capability)
            ? CapabilitySupport.Supported : declared is null ? CapabilitySupport.Unknown : CapabilitySupport.Unsupported;
        return new(model.Provider.ToString(), model.Model, model.Profile, model.Revision,
            Support(Abilities.Tools), Support(Abilities.Vision), Support(Abilities.StructuredOutput),
            profile?.ContextWindowTokens, profile?.MaximumOutputTokens, SupportFlag(profile?.SupportsTemperature),
            SupportFlag(profile?.SupportsTopP), profile?.AllowedReasoning?.ToArray());
    }

    private static string Revision(ResolvedModel resolved)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(resolved with { Revision = string.Empty }))));

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static CapabilitySupport SupportFlag(bool? value)
        => value is null ? CapabilitySupport.Unknown : value.Value ? CapabilitySupport.Supported : CapabilitySupport.Unsupported;

    private static AiProvider ParseProvider(string name)
        => Enum.TryParse<AiProvider>(name, ignoreCase: true, out var provider) && Enum.IsDefined(provider)
            && provider is not (AiProvider.FoundryLocal or AiProvider.Host or AiProvider.TypeSafe)
            ? provider : throw new ArgumentException($"Unknown chat provider '{name}'. Use OpenAI, Anthropic, Google, XAI, OpenRouter, Ollama or OpenAICompatible.", nameof(name));

    private static string? Endpoint(string? endpoint, AiProvider provider)
    {
        endpoint ??= provider switch
        {
            AiProvider.OpenAI => "https://api.openai.com/v1",
            AiProvider.Anthropic => "https://api.anthropic.com",
            AiProvider.Google => "https://generativelanguage.googleapis.com/v1beta/openai/",
            AiProvider.XAI => "https://api.x.ai/v1",
            AiProvider.OpenRouter => "https://openrouter.ai/api/v1",
            _ => null,
        };
        if (endpoint is null || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        { throw new ArgumentException("Provider endpoints must be absolute HTTP(S) URLs without credentials, query strings or fragments.", nameof(endpoint)); }
        return uri.AbsoluteUri;
    }

    private static string? NormalizeReasoning(string? value) => value?.ToLowerInvariant() switch
    {
        null => null,
        "none" => "none",
        "low" => "low",
        "medium" => "medium",
        "high" => "high",
        "xhigh" or "extrahigh" or "extra-high" => "xhigh",
        _ => throw new ArgumentException("Reasoning must be none, low, medium, high or xhigh.", nameof(value)),
    };

    private static ReasoningOptions Reasoning(string value) => new()
    {
        Effort = value switch
        {
            "none" => ReasoningEffort.None,
            "low" => ReasoningEffort.Low,
            "medium" => ReasoningEffort.Medium,
            "high" => ReasoningEffort.High,
            "xhigh" => ReasoningEffort.ExtraHigh,
            _ => throw new ArgumentException("Invalid pinned reasoning effort.", nameof(value)),
        },
    };

    public static ChatOptions CreateOptions(ResolvedModel resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        return new()
        {
            ModelId = resolved.Provider is AiProvider.Host ? null : resolved.Model,
            Reasoning = resolved.Reasoning is null ? null : Reasoning(resolved.Reasoning),
            MaxOutputTokens = resolved.MaxOutputTokens,
        };
    }

    private sealed class NonDisposing(IChatClient inner) : DelegatingChatClient(inner)
    {
        protected override void Dispose(bool disposing) { }
    }
}
