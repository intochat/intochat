using DigitalBrain;
using DigitalBrain.Brains;
using DigitalBrain.Neurons;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Runtime;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.AI.Models;

// The one facade from a role or model key to a client. Precedence:
// turn override -> person role override (IUserSettings) -> deployment RoleMap -> first available card with the role's ability.
// Delegates probing to LocalRuntimeInventory, policy to ModelDecisionPolicy, and client construction to ProviderClientBuilder.
public sealed partial class ModelCatalog
{
    private readonly IServiceProvider _services;
    private readonly IOptionsMonitor<AIOptions> _options;
    private readonly LocalRuntimeInventory _inventory;
    private readonly ProviderClientBuilder _clientBuilder;

    public ModelCatalog(IServiceProvider services, IOptionsMonitor<AIOptions> options)
        : this(services, options, localRuntimes: null)
    {
    }

    internal ModelCatalog(IServiceProvider services, IOptionsMonitor<AIOptions> options, IEnumerable<ILocalRuntime>? localRuntimes)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        _services = services;
        _options = options;
        _inventory = new LocalRuntimeInventory(services, localRuntimes);
        _clientBuilder = new ProviderClientBuilder(services, options, _inventory);
    }

    internal const string NoKeyReason = ModelDecisionPolicy.NoKeyReason;
    internal const string NotInstalledReason = ModelDecisionPolicy.NotInstalledReason;
    internal const string OllamaNotRunningReason = ModelDecisionPolicy.OllamaNotRunningReason;
    internal const string FoundryNotAvailableReason = ModelDecisionPolicy.FoundryNotAvailableReason;
    internal const string NotConfiguredReason = ModelDecisionPolicy.NotConfiguredReason;

    private string? Topology => _services.GetService<IConfiguration>()?[IntoChatOSNames.TopologyKey];

    internal bool HasProfile(string name) => _options.CurrentValue.ModelProfiles.ContainsKey(name);

    /// <summary>Applies the shared local-topology Ollama endpoint and optional local model override.</summary>
    internal static void ApplyTopologyDefaults(AIOptions ai, IConfiguration? configuration)
        => ModelDecisionPolicy.ApplyTopologyDefaults(ai, configuration);

    public Task<ResolvedModel> Resolve(ModelSelection? selection, bool requiresTools, CancellationToken ct)
        => Resolve(null, selection, requiresTools, ct);

    public Task<ResolvedModel> Resolve(string? brainId, ModelSelection? selection, bool requiresTools, CancellationToken ct)
        => Decide(brainId, ModelRole.Chat, selection, requiresTools, ct);

    /// <summary>Single model decision entry point: turn -> person -> deployment -> first available card with the role's ability.</summary>
    public Task<ResolvedModel> Decide(
        string? brainId,
        ModelRole role,
        ModelSelection? turn = null,
        bool requiresTools = false,
        CancellationToken ct = default)
        => DecideCore(brainId, role, turn, requiresTools, personSettingsReader: null, ct);

    internal Task<ResolvedModel> Decide(
        BrainId? brain,
        ModelRole role,
        ModelSelection? turn,
        bool requiresTools,
        Func<CancellationToken, Task<UserSettingsState?>>? personSettingsReader,
        CancellationToken ct)
        => DecideCore(brain?.Value, role, turn, requiresTools, personSettingsReader, ct);

    public Task<ModelCard[]> List(Abilities need = Abilities.None, CancellationToken ct = default)
        => List(brain: null, need, ct);

    public async Task<ModelCard[]> List(BrainId? brain, Abilities need = Abilities.None, CancellationToken ct = default)
    {
        var snapshot = await _inventory.ProbeAsync(ct).ConfigureAwait(false);
        return await _inventory.ListWithDeviceRoutesAsync(snapshot, brain, need, ct).ConfigureAwait(false);
    }

    internal async Task<ModelId> Pick(
        BrainId? brain,
        ModelRole role,
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
        var person = await ReadPersonSettingsAsync(brain?.Value, personSettingsReader, ct).ConfigureAwait(false);
        return ModelDecisionPolicy.EffectiveRoles(_options.CurrentValue, person);
    }

    internal async Task<ModelAvailability[]> Availability(BrainId? brain, CancellationToken ct)
    {
        var configuration = _options.CurrentValue;
        var credentials = _clientBuilder.CredentialsFor(brain?.Value);
        var local = await _inventory.ProbeAsync(ct).ConfigureAwait(false);
        var readiness = await _clientBuilder.ReadReadinessAsync(configuration, credentials, ct).ConfigureAwait(false);
        var deviceCards = brain is { } owner
            ? await _inventory.SafeRouteModelsAsync(owner, ct).ConfigureAwait(false)
            : null;

        return ModelDecisionPolicy.EvaluateAvailability(configuration, local, readiness, deviceCards);
    }

    internal Task<(IChatClient? Client, string? UnavailableReason)> TryCreateDeviceRouteClientAsync(
        string? brainId,
        ResolvedModel resolved,
        CancellationToken ct)
        => _inventory.TryCreateDeviceRouteClientAsync(brainId, resolved, ct);

    internal async Task<ResolvedModel> ResolveCloudFallbackAsync(
        string? brainId,
        ModelRole role,
        ModelSelection? selection,
        bool requiresTools,
        string deviceModelId,
        string reason,
        CancellationToken ct)
    {
        var configuration = _options.CurrentValue;
        var credentials = _clientBuilder.CredentialsFor(brainId);
        var local = await _inventory.ProbeAsync(ct).ConfigureAwait(false);
        var readiness = await _clientBuilder.ReadReadinessAsync(configuration, credentials, ct).ConfigureAwait(false);

        var decision = ModelDecisionPolicy.ResolveCloudFallback(
            role,
            selection,
            requiresTools,
            configuration,
            Topology,
            local,
            readiness,
            deviceModelId,
            reason);

        return await MaterializeDecisionAsync(decision, credentials, ct).ConfigureAwait(false);
    }

    // Escalation target after a local model keeps failing: a cloud model for the role whose key the brain or
    // deployment holds (configured cloud role, stock cloud default, then any ready cloud card). Null when no
    // cloud provider is keyed, so callers keep their local outcome instead of failing on a missing key.
    public async Task<ResolvedModel?> DecideFrontier(
        BrainId? brain,
        ModelRole role,
        bool requiresTools,
        CancellationToken ct)
    {
        var configuration = _options.CurrentValue;
        var credentials = _clientBuilder.CredentialsFor(brain?.Value);
        var local = await _inventory.ProbeAsync(ct).ConfigureAwait(false);
        var readiness = await _clientBuilder.ReadReadinessAsync(configuration, credentials, ct).ConfigureAwait(false);

        return ModelDecisionPolicy.DecideFrontier(role, requiresTools, configuration, local, readiness);
    }

    public Task<IChatClient> CreateClient(ResolvedModel resolved, CancellationToken ct, bool invokesTools = false)
        => CreateClient(null, resolved, ct, invokesTools);

    public Task<IChatClient> CreateClient(string? brainId, ResolvedModel resolved, CancellationToken ct, bool invokesTools = false)
        => _clientBuilder.CreateChatClientAsync(brainId, resolved, ct, invokesTools);

    /// <summary>Converts a <see cref="ResolvedModel"/> to its canonical <see cref="ModelSelection"/>.</summary>
    public static ModelSelection? ToSelection(ResolvedModel resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        return resolved.Provider == AiProvider.Host ? null
            : resolved.Profile is { Length: > 0 } ? new($"profile:{resolved.Profile}", resolved.Reasoning, resolved.MaxOutputTokens)
            : resolved.Provider == AiProvider.Ollama ? new($"ollama/{resolved.Model}", resolved.Reasoning, resolved.MaxOutputTokens)
            : new(resolved.Model, resolved.Reasoning, resolved.MaxOutputTokens);
    }

    /// <summary>
    /// Resolves a model for <paramref name="role"/>, verifies host-client availability, describes capabilities,
    /// and returns an <see cref="IChatClient"/> optionally wrapped with <c>UseFunctionInvocation</c>.
    /// </summary>
    public async Task<ResolvedRoleClient> ResolveRoleClientAsync(
        BrainId? brain,
        ModelRole role,
        ModelSelection? explicitSelection = null,
        bool requiresTools = false,
        bool invokesTools = false,
        Action<FunctionInvokingChatClient>? configureFunctionInvocation = null,
        int? ollamaContextWindowTokens = null,
        bool rejectDowngradedThink = false,
        CancellationToken ct = default)
    {
        var resolved = await Decide(brain?.Value, role, explicitSelection, requiresTools, ct).ConfigureAwait(false);
        if (resolved.Provider == AiProvider.Host && !_clientBuilder.HasHostChatClient)
        {
            throw new NoProviderConfiguredException(
                $"No configured tool-capable AI model or local Ollama server is available for role '{role}'.");
        }

        if (rejectDowngradedThink
            && role == ModelRole.Think
            && explicitSelection is null
            && resolved.Profile is null
            && KnownModels.Find(resolved.Model) is { Tier: not Tier.Deep } downgraded)
        {
            var expectedThink = KnownModels.Defaults.Device.TryGetValue(ModelRole.Think, out var thinkId)
                ? thinkId.Value
                : KnownModels.Qwen25Coder14B.Id.Value;
            throw new NoProviderConfiguredException(
                $"No Think/Deep model (such as '{expectedThink}') is available for coding; refusing to silently downgrade to '{downgraded.Id.Value}' ({downgraded.Tier} tier). Pull '{expectedThink}' in Ollama, configure a Think model, or set IntoChatOS:Coder:Model explicitly.");
        }

        return await BuildRoleClientAsync(
            brain,
            resolved,
            invokesTools,
            configureFunctionInvocation,
            ollamaContextWindowTokens,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves a keyed cloud frontier model for <paramref name="role"/> and returns an <see cref="IChatClient"/>,
    /// or <see langword="null"/> when no cloud provider is keyed.
    /// </summary>
    public async Task<ResolvedRoleClient?> ResolveFrontierClientAsync(
        BrainId? brain,
        ModelRole role,
        bool requiresTools = false,
        bool invokesTools = false,
        Action<FunctionInvokingChatClient>? configureFunctionInvocation = null,
        CancellationToken ct = default)
    {
        if (await DecideFrontier(brain, role, requiresTools, ct).ConfigureAwait(false) is not { } frontier)
        {
            return null;
        }

        return await BuildRoleClientAsync(
            brain,
            frontier,
            invokesTools,
            configureFunctionInvocation,
            ollamaContextWindowTokens: null,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves a role client from <paramref name="services"/> via <see cref="ModelCatalog"/> when registered,
    /// or falls back to adapting a standalone <see cref="IModelInference"/> registration when running without a catalog.
    /// </summary>
    public static async Task<ResolvedRoleClient> ResolveRoleClientAsync(
        IServiceProvider? services,
        BrainId? brain,
        ModelRole role,
        ModelSelection? explicitSelection = null,
        bool requiresTools = false,
        bool invokesTools = false,
        Action<FunctionInvokingChatClient>? configureFunctionInvocation = null,
        int? ollamaContextWindowTokens = null,
        bool rejectDowngradedThink = false,
        CancellationToken ct = default)
    {
        if (services?.GetService<ModelCatalog>() is { } catalog)
        {
            try
            {
                return await catalog.ResolveRoleClientAsync(
                    brain,
                    role,
                    explicitSelection,
                    requiresTools,
                    invokesTools,
                    configureFunctionInvocation,
                    ollamaContextWindowTokens,
                    rejectDowngradedThink,
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested
                && !rejectDowngradedThink
                && services.GetService<IModelInference>() is not null and not InferenceService
                && ex is ArgumentException or InvalidOperationException or NotSupportedException or ProviderUnavailableException)
            {
                services.GetService<ILoggerFactory>()?.CreateLogger<ModelCatalog>()
                    ?.LogWarning(ex, "ModelCatalog resolution failed for role '{Role}'; falling back to registered IModelInference seam.", role);
            }
        }

        if (services?.GetService<IModelInference>() is { } inference)
        {
            var candidates = new List<ModelSelection?>();
            if (explicitSelection is not null)
            {
                candidates.Add(explicitSelection);
            }
            candidates.Add(null);

            foreach (var candidate in candidates)
            {
                try
                {
                    var desc = await inference.Describe(candidate, model: null, cancellationToken: ct, brainId: brain?.Value).ConfigureAwait(false);
                    var isHost = string.Equals(desc.Provider, nameof(AiProvider.Host), StringComparison.OrdinalIgnoreCase);
                    if ((!requiresTools || desc.Tools == CapabilitySupport.Supported)
                        && (!isHost || services.GetService<IChatClient>() is not null))
                    {
                        var provider = Enum.TryParse<AiProvider>(desc.Provider, ignoreCase: true, out var parsed)
                            ? parsed
                            : AiProvider.Host;
                        var resolved = new ResolvedModel(provider, desc.Model, Endpoint: null, Profile: desc.Profile, Revision: desc.Revision,
                            Capabilities: desc.Tools == CapabilitySupport.Supported ? Abilities.Text | Abilities.Tools : Abilities.Text,
                            Reasoning: null, MaxOutputTokens: null);
                        var effectiveCtx = ResolveOllamaContextWindow(provider == AiProvider.Ollama, ollamaContextWindowTokens, desc);
                        IChatClient client = new InferenceChatClient(inference, candidate, brain?.Value, effectiveCtx);
                        if (invokesTools || configureFunctionInvocation is not null)
                        {
                            client = new ChatClientBuilder(client)
                                .UseFunctionInvocation(services.GetService<ILoggerFactory>(), configureFunctionInvocation)
                                .Build();
                        }

                        return new ResolvedRoleClient(client, resolved, desc, candidate);
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && ex is ArgumentException or InvalidOperationException or NotSupportedException or ProviderUnavailableException)
                {
                    services.GetService<ILoggerFactory>()?.CreateLogger<ModelCatalog>()
                        ?.LogWarning(ex, "Configured IModelInference candidate '{Model}' could not resolve.", candidate?.Model ?? "default");
                }
            }
        }

        throw new NoProviderConfiguredException(
            $"No configured tool-capable AI model or local Ollama server is available for role '{role}'.");
    }

    private async Task<ResolvedRoleClient> BuildRoleClientAsync(
        BrainId? brain,
        ResolvedModel resolved,
        bool invokesTools,
        Action<FunctionInvokingChatClient>? configureFunctionInvocation,
        int? ollamaContextWindowTokens,
        CancellationToken ct)
    {
        var selection = ToSelection(resolved);
        var inference = _services.GetService<IModelInference>();
        ModelDescriptor desc;
        IChatClient client;

        if (inference is not null and not InferenceService)
        {
            // Custom IModelInference seam (e.g. test spy/router): route through InferenceChatClient so it observes every turn.
            desc = await inference.Describe(selection, model: null, cancellationToken: ct, brainId: brain?.Value).ConfigureAwait(false);
            var effectiveCtx = ResolveOllamaContextWindow(resolved.Provider == AiProvider.Ollama, ollamaContextWindowTokens, desc);
            client = new InferenceChatClient(inference, selection, brain?.Value, effectiveCtx);
        }
        else
        {
            desc = Describe(resolved);
            var effectiveCtx = ResolveOllamaContextWindow(resolved.Provider == AiProvider.Ollama, ollamaContextWindowTokens, desc);
            client = await CreateClient(brain?.Value, resolved, ct, invokesTools: false).ConfigureAwait(false);
            if (effectiveCtx is { } ctx)
            {
                client = new ChatClientBuilder(client)
                    .ConfigureOptions(opts =>
                    {
                        opts.AdditionalProperties ??= [];
                        opts.AdditionalProperties["num_ctx"] = ctx;
                    })
                    .Build();
            }
        }

        if (invokesTools || configureFunctionInvocation is not null)
        {
            client = new ChatClientBuilder(client)
                .UseFunctionInvocation(_services.GetService<ILoggerFactory>(), configureFunctionInvocation)
                .Build();
        }

        return new ResolvedRoleClient(client, resolved, desc, selection);
    }

    private static int? ResolveOllamaContextWindow(bool isOllama, int? requestedContextWindowTokens, ModelDescriptor desc)
        => isOllama
            && requestedContextWindowTokens is { } requestedCtx and > 0
            && desc.ContextWindowTokens is { } maxCtx and > 0
            ? (int)Math.Min(requestedCtx, maxCtx)
            : null;

    public ModelDescriptor Describe(ResolvedModel model)
        => ModelDecisionPolicy.Describe(model, _options.CurrentValue);

    public static ChatOptions CreateOptions(ResolvedModel resolved)
        => ModelDecisionPolicy.CreateOptions(resolved);

    private async Task<ResolvedModel> DecideCore(
        string? brainId,
        ModelRole role,
        ModelSelection? turn,
        bool requiresTools,
        Func<CancellationToken, Task<UserSettingsState?>>? personSettingsReader,
        CancellationToken ct)
    {
        var configuration = _options.CurrentValue;
        var credentials = _clientBuilder.CredentialsFor(brainId);
        turn ??= new();

        UserSettingsState? person = null;
        var turnKey = ModelDecisionPolicy.Clean(turn.Model);
        if (turnKey is null)
        {
            person = await ReadPersonSettingsAsync(brainId, personSettingsReader, ct).ConfigureAwait(false);
        }

        var explicitKey = turnKey ?? ModelDecisionPolicy.Clean(person?.RoleOf(role)?.Value);
        var readiness = await _clientBuilder.ReadReadinessAsync(configuration, credentials, ct).ConfigureAwait(false);
        var local = ModelDecisionPolicy.NeedsLocalProbeForExplicit(explicitKey, configuration, readiness, requiresTools)
            ? await _inventory.ProbeAsync(ct).ConfigureAwait(false)
            : LocalRuntimeSnapshot.Empty;

        var decision = ModelDecisionPolicy.Decide(role, turn, person, configuration, Topology, local, readiness, requiresTools);
        return await MaterializeDecisionAsync(decision, credentials, ct).ConfigureAwait(false);
    }

    private async Task<ResolvedModel> MaterializeDecisionAsync(
        ModelDecision decision,
        IAiCredentials credentials,
        CancellationToken ct)
    {
        if (decision.MissingCredentialProvider is { } missingProvider)
        {
            var integrationId = missingProvider == AiProvider.TypeSafe
                ? AiIntegrations.TypeSafe.Id
                : AiIntegrations.IdOf(missingProvider);
            await credentials.RequireReady(integrationId, ct).ConfigureAwait(false);
        }

        if (decision.UnavailableDetail is { Length: > 0 } detail)
        {
            throw new NoProviderConfiguredException(detail);
        }

        var resolved = decision.Model
            ?? throw new NoProviderConfiguredException("No AI provider is configured.");

        if (decision.FallbackFromDeviceModelId is { Length: > 0 } deviceModelId)
        {
            _services.GetService<ILoggerFactory>()?.CreateLogger<ModelCatalog>()
                ?.LogInformation(
                    "device-fallback: device model '{DeviceModel}' fell back to cloud model '{FallbackModel}' (reason: {Reason}).",
                    deviceModelId,
                    resolved.Model,
                    decision.FallbackReason);
        }

        return resolved;
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

        if (_services.GetService<INeuronProxies>() is { } proxies)
        {
            try
            {
                return await proxies.Get<IUserSettings>(IUserSettings.Key(new BrainId(brainId))).Read().ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _services.GetService<ILoggerFactory>()?.CreateLogger<ModelCatalog>()
                    ?.LogDebug(error, "User settings for brain '{BrainId}' are unavailable; skipping person role override.", brainId);
            }
        }

        return null;
    }
}
