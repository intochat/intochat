using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DigitalBrain.Log;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Devices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Modules.AI.Models;

internal sealed record ProviderReadiness(
    ImmutableHashSet<AiProvider> ConfiguredCloudProviders,
    bool TypeSafeReady,
    bool HasHostChatClient)
{
    internal static ProviderReadiness Empty { get; } = new(
        ImmutableHashSet<AiProvider>.Empty,
        TypeSafeReady: false,
        HasHostChatClient: false);

    internal bool IsCloudConfigured(AiProvider provider) => ConfiguredCloudProviders.Contains(provider);

    internal bool AnyCloudConfigured => ConfiguredCloudProviders.Count > 0;
}

internal sealed record ModelDecision(
    ResolvedModel? Model,
    AiProvider? MissingCredentialProvider = null,
    string? UnavailableDetail = null,
    string? FallbackFromDeviceModelId = null,
    string? FallbackReason = null);

internal sealed record VoiceRoleSelection(
    ModelId? ModelId,
    ModelCard? Card,
    string? UnavailableReason);

/// <summary>
/// Pure, synchronous, deterministic model decision policy shared across Chat, Embeddings, Hear, Speak, and Talk.
/// Precedence: turn override -> person role override -> deployment RoleMap -> stock defaults -> first available card -> fallback.
/// </summary>
internal static class ModelDecisionPolicy
{
    internal const string NoKeyReason = "Add a key in API keys";
    internal const string NotInstalledReason = "Not installed";
    internal const string OllamaNotRunningReason = "Ollama is not running";
    internal const string FoundryNotAvailableReason = "Foundry Local is not available";
    internal const string NotConfiguredReason = "Not configured";

    /// <summary>Applies the shared local-topology Ollama endpoint and optional local model override.</summary>
    internal static void ApplyTopologyDefaults(AIOptions ai, IConfiguration? configuration)
    {
        ArgumentNullException.ThrowIfNull(ai);
        if (configuration is null)
        {
            return;
        }

        ApplyTopologyDefaults(
            ai,
            configuration[IntoChatOSNames.TopologyKey],
            configuration[IntoChatOSNames.LocalModelKey]);
    }

    internal static void ApplyTopologyDefaults(AIOptions ai, string? rawTopology, string? localModel)
    {
        ArgumentNullException.ThrowIfNull(ai);
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

        var cleanedModel = Clean(localModel);
        if (cleanedModel is not null && !ai.Roles.Device.ContainsKey(ModelRole.Chat))
        {
            ai.Roles.Device[ModelRole.Chat] = KnownModels.Find(cleanedModel)?.Id.Value ?? cleanedModel;
        }
    }

    internal static RoleMap EffectiveRoles(AIOptions configuration, UserSettingsState? person)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var baseMap = RoleMaps.For(configuration);
        if (person is null)
        {
            return baseMap;
        }

        var device = baseMap.Device.ToBuilder();
        var cloud = baseMap.Cloud.ToBuilder();
        var changed = false;

        foreach (var role in Enum.GetValues<ModelRole>())
        {
            if (person.RoleOf(role) is { Value.Length: > 0 } chosen)
            {
                device[role] = chosen;
                if (!DeviceModelRef.TryParse(chosen, out _))
                {
                    cloud[role] = chosen;
                }
                changed = true;
            }
        }

        return changed ? new RoleMap(device.ToImmutable(), cloud.ToImmutable()) : baseMap;
    }

    internal static bool NeedsLocalProbeForExplicit(
        string? explicitKey,
        AIOptions configuration,
        ProviderReadiness readiness,
        bool requiresTools)
    {
        if (string.IsNullOrWhiteSpace(explicitKey))
        {
            return true;
        }

        if (requiresTools && DeviceModelRef.TryParse(explicitKey, out _))
        {
            return true;
        }

        var profileNames = configuration.ModelProfiles.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!StoredModel.TryParse(explicitKey, profileNames, out var kind, out var parsedValue))
        {
            return true;
        }

        switch (kind)
        {
            case StoredModelKind.DeviceRoute:
                return false;
            case StoredModelKind.OllamaTag:
                return true;
            case StoredModelKind.Card:
                var card = KnownModels.Find(parsedValue)!;
                if (LocalRuntimeInventory.IsDeviceProvider(card.Provider))
                {
                    return true;
                }
                if (LocalRuntimeInventory.IsCloudChatProvider(card.Provider)
                    && !readiness.IsCloudConfigured(card.Provider)
                    && readiness.HasHostChatClient
                    && !readiness.AnyCloudConfigured)
                {
                    return true;
                }
                return false;
            case StoredModelKind.Profile when TryFindProfile(configuration, parsedValue, out var matchedName, out var profile):
                var provider = ParseProvider(Clean(profile.Provider) ?? throw new ArgumentException($"Model profile '{matchedName}' requires a provider."));
                if (LocalRuntimeInventory.IsDeviceProvider(provider))
                {
                    return true;
                }
                if (LocalRuntimeInventory.IsCloudChatProvider(provider)
                    && !readiness.IsCloudConfigured(provider)
                    && readiness.HasHostChatClient
                    && !readiness.AnyCloudConfigured)
                {
                    return true;
                }
                return false;
            default:
                return true;
        }
    }

    internal static (bool Ready, string? Reason) CheckCardAvailability(
        ModelCard card,
        AIOptions configuration,
        LocalRuntimeSnapshot local,
        ProviderReadiness readiness)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(readiness);

        switch (card.Provider)
        {
            case AiProvider.Ollama:
                if (local.TryGetInstalled(card.Id.Value, out var ollamaHit) && ollamaHit.Probe.Reachable)
                {
                    return (true, null);
                }
                if (local.ProviderProbes.TryGetValue(AiProvider.Ollama, out var ollamaProbe) && !ollamaProbe.AnyReachable)
                {
                    return (false, OllamaNotRunningReason);
                }
                return (false, NotInstalledReason);

            case AiProvider.FoundryLocal:
                if (local.TryGetInstalled(card.Id.Value, out var foundryHit) && foundryHit.Probe.Reachable)
                {
                    return (true, null);
                }
                if (local.ProviderProbes.TryGetValue(AiProvider.FoundryLocal, out var foundryProbe) && !foundryProbe.AnyReachable)
                {
                    return (false, FoundryNotAvailableReason);
                }
                return (false, NotInstalledReason);

            case AiProvider.SherpaOnnx:
                if (local.TryGetInstalled(card.Id.Value, out var sherpaHit) && sherpaHit.Probe.Reachable)
                {
                    return (true, null);
                }
                return (false, NotInstalledReason);

            case AiProvider.OpenAICompatible:
                if (local.TryGetInstalled(card.Id.Value, out var compatHit) && compatHit.Probe.Reachable)
                {
                    return (true, null);
                }
                if (configuration.ModelProfiles.Values.Any(p =>
                    string.Equals(p.Provider, nameof(AiProvider.OpenAICompatible), StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(p.Endpoint)))
                {
                    return (true, null);
                }
                if (local.ProviderProbes.TryGetValue(AiProvider.OpenAICompatible, out var compatProbe) && !compatProbe.AnyReachable)
                {
                    return (false, "Local server is not running");
                }
                return local.ProviderProbes.ContainsKey(AiProvider.OpenAICompatible)
                    ? (false, NotInstalledReason)
                    : (false, NotConfiguredReason);

            case AiProvider.OpenAI:
            case AiProvider.Anthropic:
            case AiProvider.Google:
            case AiProvider.XAI:
            case AiProvider.OpenRouter:
                return readiness.IsCloudConfigured(card.Provider)
                    ? (true, null)
                    : (false, NoKeyReason);

            case AiProvider.AzureFoundry:
                return readiness.IsCloudConfigured(AiProvider.AzureFoundry)
                    ? (true, null)
                    : (false, NotConfiguredReason);

            case AiProvider.TypeSafe:
                return readiness.TypeSafeReady
                    ? (true, null)
                    : (false, NoKeyReason);

            case AiProvider.Host:
                return readiness.HasHostChatClient ? (true, null) : (false, NotConfiguredReason);

            case AiProvider.Device:
                return (true, null);

            default:
                return (false, NotConfiguredReason);
        }
    }

    internal static ModelAvailability[] EvaluateAvailability(
        AIOptions configuration,
        LocalRuntimeSnapshot local,
        ProviderReadiness readiness,
        IEnumerable<ModelCard>? deviceRouteCards = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(readiness);

        var results = new List<ModelAvailability>(local.Cards.Length + 4);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < local.Cards.Length; i++)
        {
            var card = local.Cards[i];
            var (ready, reason) = CheckCardAvailability(card, configuration, local, readiness);
            results.Add(new ModelAvailability(card.Id, ready, reason));
            seen.Add(card.Id.Value);
        }

        if (deviceRouteCards is not null)
        {
            foreach (var deviceCard in deviceRouteCards)
            {
                if (seen.Add(deviceCard.Id.Value))
                {
                    results.Add(new ModelAvailability(deviceCard.Id, Ready: true, Reason: null));
                }
            }
        }

        return [.. results];
    }

    /// <summary>
    /// Single deterministic model decision entry point: turn -> person -> deployment -> stock defaults -> first available card -> fallback.
    /// </summary>
    internal static ModelDecision Decide(
        ModelRole role,
        ModelSelection? turn,
        UserSettingsState? person,
        AIOptions configuration,
        string? topology,
        LocalRuntimeSnapshot local,
        ProviderReadiness readiness,
        bool requiresTools = false)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(readiness);

        turn ??= new();

        var turnKey = Clean(turn.Model);
        var personKey = turnKey is null ? Clean(person?.RoleOf(role)?.Value) : null;

        var inheritDeploymentTuning = turnKey is null && personKey is null;
        var reasoning = Clean(turn.Reasoning) ?? (inheritDeploymentTuning ? Clean(configuration.Reasoning) : null);
        var maxTokens = turn.MaxOutputTokens ?? (inheritDeploymentTuning ? configuration.MaxOutputTokens : null);

        if (turnKey is not null || personKey is not null)
        {
            var explicitKey = turnKey ?? personKey!;
            if (requiresTools && DeviceModelRef.TryParse(explicitKey, out var toolDeviceRef))
            {
                return ResolveCloudFallback(
                    role,
                    turn,
                    requiresTools,
                    configuration,
                    topology,
                    local,
                    readiness,
                    deviceModelId: toolDeviceRef.ToModelId().Value,
                    reason: "tools-unsupported");
            }

            return ResolveExplicit(
                explicitKey,
                turn,
                configuration,
                local,
                readiness,
                reasoning,
                maxTokens,
                requiresTools,
                paramName: turnKey is not null ? nameof(turn) : "person");
        }

        var cardsById = new Dictionary<string, ModelCard>(local.Cards.Length, StringComparer.OrdinalIgnoreCase);
        foreach (var card in local.Cards)
        {
            cardsById.TryAdd(card.Id.Value, card);
        }

        var needed = RoleNeeds.Of(role) | (requiresTools ? Abilities.Tools : Abilities.None);
        var profileNames = configuration.ModelProfiles.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 1. Explicit deployment role configuration for the active topology.
        string? firstExplicitDeployment = null;
        foreach (var candidateId in RoleMaps.Candidates(configuration.Roles.ToRoleMap(), topology, role))
        {
            firstExplicitDeployment ??= candidateId.Value;

            if (StoredModel.TryParse(candidateId.Value, profileNames, out var kind, out var parsedValue))
            {
                if (kind == StoredModelKind.DeviceRoute)
                {
                    if (!requiresTools)
                    {
                        return ResolveExplicit(candidateId.Value, turn, configuration, local, readiness, reasoning, maxTokens, requiresTools, nameof(role));
                    }
                    continue;
                }

                if (kind == StoredModelKind.Profile && TryFindProfile(configuration, parsedValue, out var matchedName, out var profile))
                {
                    var profileProvider = ParseProvider(Clean(profile.Provider) ?? throw new ArgumentException($"Model profile '{matchedName}' requires a provider."));
                    var profileEndpoint = Clean(profile.Endpoint);
                    if (IsProviderReadyForExplicit(profileProvider, profileEndpoint, local, readiness))
                    {
                        return ResolveExplicit(candidateId.Value, turn, configuration, local, readiness, reasoning, maxTokens, requiresTools, nameof(role));
                    }
                    continue;
                }

                if (kind == StoredModelKind.Card && cardsById.TryGetValue(parsedValue, out var explicitCard))
                {
                    var (ready, _) = CheckCardAvailability(explicitCard, configuration, local, readiness);
                    if (ready)
                    {
                        return ResolveCard(explicitCard, configuration, local, readiness, reasoning, maxTokens, requiresTools);
                    }
                    continue;
                }

                if (kind == StoredModelKind.OllamaTag)
                {
                    var tagCard = cardsById.TryGetValue(parsedValue, out var found)
                        ? found
                        : MakeDynamicOllamaCard(parsedValue);
                    var (ready, _) = CheckCardAvailability(tagCard, configuration, local, readiness);
                    if (ready)
                    {
                        return ResolveCard(tagCard, configuration, local, readiness, reasoning, maxTokens, requiresTools);
                    }
                    continue;
                }
            }
            else if (cardsById.TryGetValue(candidateId.Value, out var localCandidate))
            {
                var (ready, _) = CheckCardAvailability(localCandidate, configuration, local, readiness);
                if (ready)
                {
                    return ResolveCard(localCandidate, configuration, local, readiness, reasoning, maxTokens, requiresTools);
                }
            }
        }

        // 2. Stock deployment defaults (KnownModels.Defaults) for the active topology.
        foreach (var defaultId in RoleMaps.Candidates(RoleMaps.DefaultsFor(configuration), topology, role))
        {
            if (cardsById.TryGetValue(defaultId.Value, out var defaultCard)
                && (defaultCard.Can & needed) == needed)
            {
                var (ready, _) = CheckCardAvailability(defaultCard, configuration, local, readiness);
                if (ready)
                {
                    return ResolveCard(defaultCard, configuration, local, readiness, reasoning, maxTokens, requiresTools);
                }
            }
        }

        // 3. First available card declaring the role's required abilities.
        foreach (var card in local.Cards)
        {
            if ((card.Can & needed) != needed)
            {
                continue;
            }

            var (ready, _) = CheckCardAvailability(card, configuration, local, readiness);
            if (ready)
            {
                return ResolveCard(card, configuration, local, readiness, reasoning, maxTokens, requiresTools);
            }
        }

        // 4. Fallback when no card is available: explicit deployment choice (to surface its error) or Host client (in tests).
        if (firstExplicitDeployment is not null)
        {
            return ResolveExplicit(firstExplicitDeployment, turn, configuration, local, readiness, reasoning, maxTokens, requiresTools, nameof(role));
        }

        var host = new ResolvedModel(AiProvider.Host, "default", null, null, string.Empty, Abilities.Text | Abilities.Tools, NormalizeReasoning(reasoning), maxTokens);
        return new ModelDecision(host with { Revision = ComputeRevision(host) });
    }

    internal static ModelDecision ResolveCloudFallback(
        ModelRole role,
        ModelSelection? selection,
        bool requiresTools,
        AIOptions configuration,
        string? topology,
        LocalRuntimeSnapshot local,
        ProviderReadiness readiness,
        string? deviceModelId = null,
        string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(readiness);

        var fallbackTurn = new ModelSelection(null, selection?.Reasoning, selection?.MaxOutputTokens);
        var reasoning = Clean(selection?.Reasoning) ?? Clean(configuration.Reasoning);
        var maxTokens = selection?.MaxOutputTokens ?? configuration.MaxOutputTokens;
        var profileNames = configuration.ModelProfiles.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        ModelDecision? decision = null;
        var rolesMap = configuration.Roles.ToRoleMap();
        if (rolesMap.Cloud.TryGetValue(role, out var cloudRoleModel)
            && !string.IsNullOrWhiteSpace(cloudRoleModel.Value)
            && !DeviceModelRef.TryParse(cloudRoleModel, out _)
            && CanResolveExplicitCloud(cloudRoleModel.Value, profileNames, configuration, local, readiness))
        {
            decision = ResolveExplicit(
                cloudRoleModel.Value,
                fallbackTurn,
                configuration,
                local,
                readiness,
                reasoning,
                maxTokens,
                requiresTools,
                nameof(role));
        }

        if (decision is null
            && KnownModels.Defaults.Cloud.TryGetValue(role, out var defaultCloud)
            && !DeviceModelRef.TryParse(defaultCloud, out _)
            && CanResolveExplicitCloud(defaultCloud.Value, profileNames, configuration, local, readiness))
        {
            decision = ResolveExplicit(
                defaultCloud.Value,
                fallbackTurn,
                configuration,
                local,
                readiness,
                reasoning,
                maxTokens,
                requiresTools,
                nameof(role));
        }

        decision ??= Decide(
            role,
            fallbackTurn,
            person: null,
            configuration,
            topology,
            local,
            readiness,
            requiresTools);

        return decision with
        {
            FallbackFromDeviceModelId = deviceModelId,
            FallbackReason = reason,
        };
    }

    internal static ResolvedModel? DecideFrontier(
        ModelRole role,
        bool requiresTools,
        AIOptions configuration,
        LocalRuntimeSnapshot local,
        ProviderReadiness readiness)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(readiness);

        var reasoning = Clean(configuration.Reasoning);
        var maxTokens = configuration.MaxOutputTokens;
        var needed = RoleNeeds.Of(role) | (requiresTools ? Abilities.Tools : Abilities.None);

        var candidates = new List<ModelCard>(local.Cards.Length + 2);
        if (configuration.Roles.ToRoleMap().Cloud.TryGetValue(role, out var configured)
            && KnownModels.Find(configured.Value) is { } configuredCard)
        {
            candidates.Add(configuredCard);
        }

        if (KnownModels.Defaults.Cloud.TryGetValue(role, out var stock) && KnownModels.Find(stock.Value) is { } stockCard)
        {
            candidates.Add(stockCard);
        }

        candidates.AddRange(local.Cards);
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in candidates)
        {
            if (!LocalRuntimeInventory.IsCloudChatProvider(card.Provider) || (card.Can & needed) != needed || !tried.Add(card.Id.Value))
            {
                continue;
            }

            var (ready, _) = CheckCardAvailability(card, configuration, local, readiness);
            if (!ready)
            {
                continue;
            }

            try
            {
                var decision = ResolveCard(card, configuration, local, readiness, reasoning, maxTokens, requiresTools);
                if (decision.Model is { } resolved && LocalRuntimeInventory.IsCloudChatProvider(resolved.Provider))
                {
                    return resolved;
                }
            }
            catch (ArgumentException error)
            {
                // The card cannot honour this deployment's tuning (e.g. reasoning with tools); try the next one.
                OsConsoleLog.For<ModelCatalog>().LogSwallowed(error, $"resolving frontier candidate '{card.Id.Value}'");
            }
        }

        return null;
    }

    internal static VoiceRoleSelection SelectTalkBinding(RoleMap roles, string? topology)
    {
        ArgumentNullException.ThrowIfNull(roles);
        var binding = RoleMaps.Deployment(roles, topology, ModelRole.Talk).FirstOrDefault();
        if (binding is not { Value.Length: > 0 })
        {
            return new VoiceRoleSelection(null, null, null);
        }

        var card = KnownModels.Find(binding.Value);
        if (card is null || !card.Can.HasFlag(Abilities.Talk))
        {
            return new VoiceRoleSelection(binding, card, $"{card?.Name ?? binding.Value} cannot talk in this build.");
        }

        return new VoiceRoleSelection(card.Id, card, UnavailableReason: null);
    }

    internal static string? DescribeMissingVoiceRole(RoleMap roles, string? topology, ModelRole role)
    {
        ArgumentNullException.ThrowIfNull(roles);
        var candidate = RoleMaps.Deployment(roles, topology, role).FirstOrDefault();
        if (candidate is not { Value.Length: > 0 })
        {
            return null;
        }

        var card = KnownModels.Find(candidate.Value);
        if (card is null)
        {
            return null;
        }

        return card.Where == Where.Device
            ? $"{card.Name} is not installed for the {role} role."
            : $"{card.Name} is not configured for the {role} role.";
    }

    internal static ModelDescriptor Describe(ResolvedModel model, AIOptions configuration)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(configuration);

        var profile = model.Profile is not null ? configuration.ModelProfiles.GetValueOrDefault(model.Profile) : null;
        if (profile?.ContextWindowTokens is <= 0 || profile?.MaximumOutputTokens is <= 0 || profile?.MaximumOutputTokens > profile?.ContextWindowTokens)
        {
            throw new ArgumentException("Configured model token limits must be positive and output cannot exceed context.");
        }
        if (profile?.AllowedReasoning?.Any(value => value is not ("none" or "low" or "medium" or "high" or "xhigh")) == true)
        {
            throw new ArgumentException("AllowedReasoning contains an unsupported reasoning value.");
        }

        var declared = profile?.Capabilities;
        CapabilitySupport Support(Abilities capability) => model.Capabilities.HasFlag(capability)
            ? CapabilitySupport.Supported : declared is null ? CapabilitySupport.Unknown : CapabilitySupport.Unsupported;

        var card = KnownModels.Find(model.Model);
        if (card is not null && card.Provider != model.Provider)
        {
            card = null;
        }

        var contextWindowTokens = profile?.ContextWindowTokens ?? card?.ContextWindowTokens;
        return new(model.Provider.ToString(), model.Model, model.Profile, model.Revision,
            Support(Abilities.Tools), Support(Abilities.Vision), Support(Abilities.StructuredOutput),
            contextWindowTokens, profile?.MaximumOutputTokens, SupportFlag(profile?.SupportsTemperature),
            SupportFlag(profile?.SupportsTopP), profile?.AllowedReasoning?.ToArray());
    }

    internal static ChatOptions CreateOptions(ResolvedModel resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        return new()
        {
            ModelId = resolved.Provider is AiProvider.Host ? null : resolved.Model,
            Reasoning = resolved.Reasoning is null ? null : ToReasoningOptions(resolved.Reasoning),
            MaxOutputTokens = resolved.MaxOutputTokens,
        };
    }

    internal static string ComputeRevision(ResolvedModel resolved)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(resolved with { Revision = string.Empty }))));

    internal static void ValidateRevision(ResolvedModel resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        if (!string.Equals(resolved.Revision, ComputeRevision(resolved), StringComparison.Ordinal))
        {
            throw new ArgumentException("The resolved model configuration does not match its revision.", nameof(resolved));
        }
    }

    internal static ReasoningOptions ToReasoningOptions(string value) => new()
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

    internal static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ModelDecision ResolveExplicit(
        string key,
        ModelSelection turn,
        AIOptions configuration,
        LocalRuntimeSnapshot local,
        ProviderReadiness readiness,
        string? reasoning,
        int? maxTokens,
        bool requiresTools,
        string paramName)
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
                case StoredModelKind.DeviceRoute:
                    provider = AiProvider.Device;
                    modelId = parsedValue;
                    declared = Abilities.Text;
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
            var localCard = local.Cards.FirstOrDefault(c => string.Equals(c.Id.Value, key, StringComparison.OrdinalIgnoreCase));
            if (localCard is null)
            {
                throw new ArgumentException($"Unknown model '{key}'. Use a catalogued model id or a configured profile name.", paramName);
            }

            preset = localCard;
            provider = localCard.Provider;
            modelId = localCard.Id.Value;
            declared = localCard.Can;
        }

        return FinalizeResolution(
            provider,
            modelId,
            endpoint,
            profileName,
            declared,
            preset,
            configuration,
            local,
            readiness,
            reasoning,
            maxTokens,
            requiresTools);
    }

    private static ModelDecision ResolveCard(
        ModelCard card,
        AIOptions configuration,
        LocalRuntimeSnapshot local,
        ProviderReadiness readiness,
        string? reasoning,
        int? maxTokens,
        bool requiresTools)
        => FinalizeResolution(
            card.Provider,
            card.Id.Value,
            endpoint: null,
            profileName: null,
            declared: card.Can,
            preset: card,
            configuration,
            local,
            readiness,
            reasoning,
            maxTokens,
            requiresTools);

    private static ModelDecision FinalizeResolution(
        AiProvider provider,
        string modelId,
        string? endpoint,
        string? profileName,
        Abilities? declared,
        ModelCard? preset,
        AIOptions configuration,
        LocalRuntimeSnapshot local,
        ProviderReadiness readiness,
        string? reasoning,
        int? maxTokens,
        bool requiresTools)
    {
        if (provider == AiProvider.Ollama && preset is not null)
        {
            modelId = TryResolveConfiguredOllamaTag(configuration.Ollama, preset) ?? modelId;
        }

        if (modelId.Length > 200 || modelId.Any(char.IsControl))
        {
            throw new ArgumentException("Model ID must contain at most 200 printable characters.", nameof(modelId));
        }

        if (LocalRuntimeInventory.IsCloudChatProvider(provider))
        {
            if (!readiness.IsCloudConfigured(provider))
            {
                if (readiness.HasHostChatClient && !AnyProviderConfigured(configuration, local, readiness))
                {
                    var host = new ResolvedModel(AiProvider.Host, modelId, null, profileName, string.Empty,
                        declared ?? preset?.Can ?? (Abilities.Text | Abilities.Tools), NormalizeReasoning(reasoning), maxTokens);
                    return new ModelDecision(host with { Revision = ComputeRevision(host) });
                }

                return new ModelDecision(Model: null, MissingCredentialProvider: provider);
            }

            endpoint ??= Clean(configuration.Provider(provider).Endpoint);
            if (endpoint is not null)
            {
                endpoint = NormalizeEndpoint(endpoint, provider);
            }
        }
        else if (LocalRuntimeInventory.IsDeviceProvider(provider))
        {
            var hasInstalled = local.TryGetInstalled(modelId, out var installedHit) && installedHit.Probe.Reachable;
            var hasReachableRuntime = local.IsProviderReachable(provider, out var firstEndpoint);

            if (hasInstalled)
            {
                endpoint ??= installedHit.Probe.Endpoint?.AbsoluteUri;
                declared ??= installedHit.Card.Can;
            }
            else if (endpoint is not null)
            {
                endpoint = NormalizeEndpoint(endpoint, provider);
            }
            else if (hasReachableRuntime)
            {
                endpoint = firstEndpoint?.AbsoluteUri;
            }
            else if (readiness.HasHostChatClient && !AnyProviderConfigured(configuration, local, readiness))
            {
                var host = new ResolvedModel(AiProvider.Host, modelId, null, profileName, string.Empty,
                    declared ?? preset?.Can ?? (Abilities.Text | Abilities.Tools), NormalizeReasoning(reasoning), maxTokens);
                return new ModelDecision(host with { Revision = ComputeRevision(host) });
            }
            else
            {
                var detail = provider == AiProvider.Ollama
                    ? $"Provider 'Ollama' is not running or model '{modelId}' is not installed."
                    : $"Provider '{provider}' is not available for model '{modelId}'.";
                return new ModelDecision(Model: null, UnavailableDetail: detail);
            }

            if (endpoint is not null)
            {
                endpoint = NormalizeEndpoint(endpoint, provider);
            }
        }
        else if (provider == AiProvider.TypeSafe)
        {
            if (!readiness.TypeSafeReady)
            {
                return new ModelDecision(Model: null, MissingCredentialProvider: AiProvider.TypeSafe);
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
        if (reasoning is not null && profileName is null && !capabilities.HasFlag(Abilities.Reasoning))
        {
            if (provider != AiProvider.Ollama || string.Equals(reasoning, "xhigh", StringComparison.Ordinal))
            {
                reasoning = null;
            }
        }

        if (requiresTools && provider == AiProvider.OpenAI && modelId.StartsWith("gpt-5.6", StringComparison.OrdinalIgnoreCase) && reasoning is null)
        {
            reasoning = "none";
        }

        if (maxTokens is < 1 or > 1_000_000)
        {
            throw new ArgumentException("MaxOutputTokens must be between 1 and 1000000.");
        }

        var resolved = new ResolvedModel(provider, modelId, endpoint, profileName, string.Empty, capabilities, reasoning, maxTokens);
        return new ModelDecision(resolved with { Revision = ComputeRevision(resolved) });
    }

    private static bool AnyProviderConfigured(AIOptions configuration, LocalRuntimeSnapshot local, ProviderReadiness readiness)
        => configuration.ModelProfiles.Values.Any(profile => !string.IsNullOrWhiteSpace(profile.Endpoint))
            || readiness.AnyCloudConfigured
            || local.AnyDeviceChatReachable();

    private static bool IsProviderReadyForExplicit(
        AiProvider provider,
        string? explicitEndpoint,
        LocalRuntimeSnapshot local,
        ProviderReadiness readiness)
    {
        if (LocalRuntimeInventory.IsDeviceProvider(provider))
        {
            if (!string.IsNullOrWhiteSpace(explicitEndpoint))
            {
                return true;
            }

            return local.IsProviderReachable(provider, out _);
        }

        return LocalRuntimeInventory.IsCloudChatProvider(provider) && readiness.IsCloudConfigured(provider);
    }

    private static bool CanResolveExplicitCloud(
        string key,
        IReadOnlySet<string> profileNames,
        AIOptions configuration,
        LocalRuntimeSnapshot local,
        ProviderReadiness readiness)
    {
        if (!StoredModel.TryParse(key, profileNames, out var kind, out var parsed))
        {
            return false;
        }

        if (kind == StoredModelKind.Profile && TryFindProfile(configuration, parsed, out _, out var profile))
        {
            var provider = ParseProvider(Clean(profile.Provider) ?? string.Empty);
            return readiness.HasHostChatClient
                || IsProviderReadyForExplicit(provider, Clean(profile.Endpoint), local, readiness);
        }

        if (kind == StoredModelKind.Card && KnownModels.Find(parsed) is { } card)
        {
            if (readiness.HasHostChatClient && !AnyProviderConfigured(configuration, local, readiness))
            {
                return true;
            }

            var (ready, _) = CheckCardAvailability(card, configuration, local, readiness);
            return ready;
        }

        return false;
    }

    private static ModelCard MakeDynamicOllamaCard(string tag)
    {
        var colon = tag.IndexOf(':');
        var family = colon > 0 ? tag[..colon] : tag;
        return new ModelCard(new ModelId(tag), AiProvider.Ollama, family, tag, Where.Device, Abilities.Text | Abilities.Tools, Tier.Balanced);
    }

    private static string? TryResolveConfiguredOllamaTag(OllamaOptions ollama, ModelCard card)
    {
        if (ollama.Models.TryGetValue(card.Id.Value, out var direct) && !string.IsNullOrWhiteSpace(direct.Model))
        {
            return direct.Model.Trim();
        }

        foreach (var (key, entry) in ollama.Models)
        {
            if (KnownModels.Find(key)?.Id == card.Id && !string.IsNullOrWhiteSpace(entry.Model))
            {
                return entry.Model.Trim();
            }
        }

        return null;
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

    private static CapabilitySupport SupportFlag(bool? value)
        => value is null ? CapabilitySupport.Unknown : value.Value ? CapabilitySupport.Supported : CapabilitySupport.Unsupported;

    private static AiProvider ParseProvider(string name)
        => Enum.TryParse<AiProvider>(name, ignoreCase: true, out var provider) && Enum.IsDefined(provider)
            && provider is not (AiProvider.Host or AiProvider.TypeSafe or AiProvider.Device)
            ? provider : throw new ArgumentException($"Unknown chat provider '{name}'. Use OpenAI, Anthropic, Google, XAI, OpenRouter, AzureFoundry, Ollama, FoundryLocal or OpenAICompatible.", nameof(name));

    private static string? NormalizeEndpoint(string? endpoint, AiProvider provider)
    {
        if (provider == AiProvider.AzureFoundry)
        {
            return AzureFoundry.AzureFoundryProviderFactory.NormalizeEndpoint(endpoint).AbsoluteUri;
        }

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
        {
            throw new ArgumentException("Provider endpoints must be absolute HTTP(S) URLs without credentials, query strings or fragments.", nameof(endpoint));
        }

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
}
