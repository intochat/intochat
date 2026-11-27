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
public sealed class ModelCatalog
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
