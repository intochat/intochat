using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities.Compute;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models.Ollama;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Host.Visuals;

namespace IntoChat.OS.Modules.AI.Models;

// The one resolver from a model key to a client. A key is a marker name, a model ID, or a configured profile
// name; null is the deployment default. brainId is the calling brain: its own provider keys make
// models available to it alone and win over the deployment registration. A null brain sees only the
// deployment's registrations.
public sealed class ModelCatalog(IServiceProvider services, IOptionsMonitor<AIOptions> options)
{
    internal static LLMModel DefaultChatModel { get; } =
        LLMModel.FindByMarker(typeof(IQwen35))
        ?? throw new InvalidOperationException("Catalogued default chat model IQwen35 is missing.");

    public static string DefaultChatModelId => DefaultChatModel.Id;

    private IAiCredentials Deployment => services.GetRequiredService<IAiCredentials>();

    private IAiCredentials CredentialsFor(string? brainId)
        => brainId is not null && BrainScope.IsValidId(brainId) && services.GetService<BrainKeyAvailability>() is { } keys
            ? new BrainAwareCredentials(Deployment, keys, brainId)
            : Deployment;

    internal bool HasProfile(string name) => options.CurrentValue.ModelProfiles.ContainsKey(name);

    private bool HasHostChatClient => services.GetService<IChatClient>() is not null and not RebuildingChatClient;

    internal Task<LocalOllamaDetection?> DetectLocal(CancellationToken ct)
        => services.GetService<ILocalOllamaProbe>() is { } probe
            ? probe.Detect(ct)
            : Task.FromResult<LocalOllamaDetection?>(null);

    /// <summary>Applies the shared local-topology Ollama endpoint and chat model defaults when configured.</summary>
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

        if (string.IsNullOrWhiteSpace(ai.Default.Model))
        {
            ai.Default.Model = DefaultChatModel.Marker.Name;
        }

        var localModel = Clean(configuration[IntoChatOSNames.LocalModelKey]);
        if (localModel is not null && !ai.Ollama.Models.ContainsKey(DefaultChatModel.Marker.Name))
        {
            ai.Ollama.Models[DefaultChatModel.Marker.Name] = new AIModelOptions { Model = localModel };
        }
    }

    public Task<ResolvedModel> Resolve(ModelSelection? selection, bool requiresTools, CancellationToken ct)
        => Resolve(null, selection, requiresTools, ct);

    public Task<ResolvedModel> Resolve(string? brainId, ModelSelection? selection, bool requiresTools, CancellationToken ct)
        => Decide(brainId, ModelUse.Chat, selection, personModel: null, requiresTools, ct);

    /// <summary>Single model decision entry point with precedence turn -> person -> deployment -> detected.</summary>
    public async Task<ResolvedModel> Decide(
        string? brainId,
        ModelUse use,
        ModelSelection? turn = null,
        string? personModel = null,
        bool requiresTools = false,
        CancellationToken ct = default)
    {
        var credentials = CredentialsFor(brainId);
        turn ??= new();
        var configuration = options.CurrentValue;

        var turnKey = Clean(turn.Model);
        var personKey = Clean(personModel);
        var deploymentKey = Clean(configuration.Default.Model);
        var key = turnKey ?? personKey ?? deploymentKey;
        var fromTurn = turnKey is not null;
        var fromDeploymentOnly = turnKey is null && personKey is null && deploymentKey is not null;
        var inheritDeploymentTuning = turnKey is null && personKey is null;

        var reasoning = Clean(turn.Reasoning) ?? (inheritDeploymentTuning ? Clean(configuration.Default.Reasoning) : null);
        var maxTokens = turn.MaxOutputTokens ?? (inheritDeploymentTuning ? configuration.Default.MaxOutputTokens : null);

        if (fromDeploymentOnly
            && use == ModelUse.Code
            && !HasHostChatClient
            && IsGenericLocalChatDeployment(configuration, deploymentKey!)
            && IsDefaultOrLoopbackOllamaEndpoint(configuration.Ollama.Endpoint)
            && await DetectLocal(ct).ConfigureAwait(false) is { InstalledModels.Length: > 0 } localForCode)
        {
            var coderTag = HttpLocalOllamaProbe.SelectPreferredCoderModel(localForCode.InstalledModels);
            if (HttpLocalOllamaProbe.IsCoderModelTag(coderTag))
            {
                var coderEndpoint = Endpoint(Clean(configuration.Ollama.Endpoint) ?? localForCode.Endpoint, AiProvider.Ollama);
                var coderResolved = new ResolvedModel(
                    AiProvider.Ollama,
                    coderTag,
                    coderEndpoint,
                    null,
                    string.Empty,
                    LlmCapabilities.Tools,
                    NormalizeReasoning(reasoning),
                    maxTokens);
                return coderResolved with { Revision = Revision(coderResolved) };
            }
        }

        AiProvider provider;
        string modelId;
        string? endpoint = null;
        string? profileName = null;
        LlmCapabilities? declared = null;
        LLMModel? preset = null;
        if (key is null)
        {
            // Automatic: lowest list input price among configured tool-capable models; ties prefer stock chat default.
            var prices = services.GetService<IPriceList>();
            foreach (var candidate in LLMModel.All
                .Where(model => !requiresTools || model.SupportsTools)
                .OrderBy(model => InputPrice(prices, model))
                .ThenBy(model => model == DefaultChatModel ? 0 : 1))
            {
                if (await AIClients.Factory(candidate.Provider).IsConfigured(configuration, credentials, ct))
                {
                    preset = candidate;
                    break;
                }
            }

            if (preset is null)
            {
                if (!HasHostChatClient
                    && string.IsNullOrWhiteSpace(configuration.Ollama.Endpoint)
                    && await DetectLocal(ct).ConfigureAwait(false) is { InstalledModels.Length: > 0 } localAuto)
                {
                    provider = AiProvider.Ollama;
                    modelId = use == ModelUse.Code
                        ? HttpLocalOllamaProbe.SelectPreferredCoderModel(localAuto.InstalledModels)
                        : HttpLocalOllamaProbe.SelectPreferredModel(localAuto.InstalledModels);
                    endpoint = Endpoint(localAuto.Endpoint, AiProvider.Ollama);
                    declared = LlmCapabilities.Tools;
                }
                else
                {
                    var host = new ResolvedModel(AiProvider.Host, "default", null, null, string.Empty, LlmCapabilities.Tools, reasoning, maxTokens);
                    return host with { Revision = Revision(host) };
                }
            }
            else if (preset.Provider == AiProvider.Ollama
                && use == ModelUse.Code
                && !HasHostChatClient
                && IsDefaultOrLoopbackOllamaEndpoint(configuration.Ollama.Endpoint)
                && await DetectLocal(ct).ConfigureAwait(false) is { InstalledModels.Length: > 0 } localCodeAuto)
            {
                var coderTag = HttpLocalOllamaProbe.SelectPreferredCoderModel(localCodeAuto.InstalledModels);
                provider = AiProvider.Ollama;
                modelId = HttpLocalOllamaProbe.IsCoderModelTag(coderTag) ? coderTag : preset.Id;
                declared = preset.Capabilities;
            }
            else
            {
                provider = preset.Provider;
                modelId = preset.Id;
            }
        }
        else
        {
            var profileNames = configuration.ModelProfiles.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            ModelRef? parsed = null;
            if (ModelRef.TryReadStored(key, profileNames, detectedLocalTags: null, out var candidateRef))
            {
                parsed = candidateRef;
            }
            else
            {
                var probedTags = await DetectLocal(ct).ConfigureAwait(false);
                if (probedTags is { InstalledModels.Length: > 0 }
                    && ModelRef.TryReadStored(key, profileNames, probedTags.InstalledModels, out var localRef))
                {
                    parsed = localRef;
                }
            }
            if (parsed is null)
            {
                throw new ArgumentException($"Unknown model '{key}'. Use a model marker name or a configured profile name.", fromTurn ? nameof(turn) : nameof(personModel));
            }

            switch (parsed)
            {
                case ModelRef.Profile p when TryFindProfile(configuration, p.Name, out var matchedProfileName, out var profile):
                    profileName = matchedProfileName;
                    provider = ParseProvider(Clean(profile.Provider) ?? throw new ArgumentException($"Model profile '{matchedProfileName}' requires a provider."));
                    modelId = Clean(profile.Model) ?? throw new ArgumentException($"Model profile '{matchedProfileName}' requires a model ID.");
                    endpoint = Clean(profile.Endpoint);
                    reasoning = Clean(turn.Reasoning) ?? Clean(profile.Reasoning);
                    maxTokens = turn.MaxOutputTokens ?? profile.MaxOutputTokens;
                    declared = profile.Capabilities;
                    preset = LLMModel.Find(modelId);
                    if (preset is not null && preset.Provider != provider) { preset = null; }
                    break;
                case ModelRef.Catalogued c:
                    preset = c.Model;
                    provider = c.Model.Provider;
                    modelId = c.Model.Id;
                    break;
                case ModelRef.Tag t:
                    provider = t.Provider;
                    modelId = t.Id;
                    declared = ModelRef.DefaultCapabilitiesFor(t, key);
                    preset = LLMModel.Find(modelId);
                    if (preset is not null && preset.Provider != provider) { preset = null; }
                    break;
                default:
                    throw new ArgumentException($"Unknown model '{key}'. Use a model marker name or a configured profile name.");
            }
        }

        if (provider == AiProvider.Ollama && preset is not null)
        { modelId = Clean(configuration.Ollama.Models.GetValueOrDefault(preset.Marker.Name)?.Model) ?? modelId; }
        if (modelId.Length > 200 || modelId.Any(char.IsControl))
        { throw new ArgumentException("Model ID must contain at most 200 printable characters.", nameof(turn)); }

        var factory = AIClients.Factory(provider);
        if (!await factory.IsConfigured(configuration, credentials, ct) && !(provider == AiProvider.Ollama && endpoint is not null))
        {
            // When running against a test/host-supplied IChatClient with no external providers configured,
            // preserve the requested model ID on the Host client so dynamic model selection is observable.
            if (HasHostChatClient && !await AnyProviderConfigured(configuration, credentials, ct))
            {
                var host = new ResolvedModel(AiProvider.Host, modelId, null, profileName, string.Empty, declared ?? preset?.Capabilities ?? LlmCapabilities.Tools, NormalizeReasoning(reasoning), maxTokens);
                return host with { Revision = Revision(host) };
            }
            if (provider == AiProvider.Ollama)
            {
                if (await DetectLocal(ct).ConfigureAwait(false) is { } localDetected)
                {
                    endpoint = localDetected.Endpoint;
                    if (!localDetected.InstalledModels.Any(tag => string.Equals(tag, modelId, StringComparison.OrdinalIgnoreCase)))
                    {
                        modelId = use == ModelUse.Code
                            ? HttpLocalOllamaProbe.SelectPreferredCoderModel(localDetected.InstalledModels)
                            : HttpLocalOllamaProbe.SelectPreferredModel(localDetected.InstalledModels);
                    }
                    declared ??= LlmCapabilities.Tools;
                }
                else
                {
                    throw new NoProviderConfiguredException($"Provider 'Ollama' is not configured. Set {AIKeys.OllamaEndpoint}.");
                }
            }
            else
            {
                await credentials.RequireReady(AiIntegrations.IdOf(provider), ct);
            }
        }
        // Integration endpoints and keys are materialized together when the client is used; persist
        // only explicit host/profile overrides, where null means the provider integration default.
        endpoint ??= provider == AiProvider.Ollama ? Clean(configuration.Ollama.Endpoint) : Clean(configuration.Provider(provider).Endpoint);
        if (endpoint is not null || provider == AiProvider.Ollama) { endpoint = Endpoint(endpoint, provider); }

        var capabilities = declared ?? preset?.Capabilities ?? LlmCapabilities.None;
        if ((capabilities & ~(LlmCapabilities.Tools | LlmCapabilities.Vision | LlmCapabilities.StructuredOutput)) != 0)
        { throw new ArgumentException("Model capabilities contain unknown flags.", nameof(turn)); }
        if (requiresTools && !capabilities.HasFlag(LlmCapabilities.Tools))
        { throw new ArgumentException($"Model '{modelId}' does not declare tool support. Configure Tools capabilities in its model profile before attaching tools.", nameof(turn)); }
        reasoning = NormalizeReasoning(reasoning);
        if (requiresTools && provider == AiProvider.OpenAI && modelId.StartsWith("gpt-5.6", StringComparison.OrdinalIgnoreCase))
        {
            if (reasoning is not null and not "none") { throw new ArgumentException("GPT-5.6 tools through OpenAI Chat Completions require reasoning 'none'.", nameof(turn)); }
            reasoning = "none";
        }
        if (maxTokens is < 1 or > 1_000_000) { throw new ArgumentException("MaxOutputTokens must be between 1 and 1000000.", nameof(turn)); }
        var resolved = new ResolvedModel(provider, modelId, endpoint, profileName, string.Empty, capabilities, reasoning, maxTokens);
        return resolved with { Revision = Revision(resolved) };
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
            if (await AIClients.Factory(provider).IsConfigured(configuration, credentials, ct)) { return true; }
        }
        return false;
    }

    private static bool IsGenericLocalChatDeployment(AIOptions configuration, string deploymentKey)
        => LLMModel.Find(deploymentKey) == DefaultChatModel
            && !configuration.Ollama.Models.ContainsKey(DefaultChatModel.Marker.Name);

    private static bool IsDefaultOrLoopbackOllamaEndpoint(string? endpoint)
        => string.IsNullOrWhiteSpace(endpoint)
            || (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.IsLoopback && uri.Port == 11434);

    // By default the pipeline carries GenAI telemetry and metering but never function invocation:
    // inference returns proposed calls and the agent owns the only tool loop. invokesTools is for
    // a caller that hands the client AIFunctions and expects it to run them; it also rejects tools
    // the model does not declare. The Host client is the deployment's and is not owned by the caller.
    public Task<IChatClient> CreateClient(ResolvedModel resolved, CancellationToken ct, bool invokesTools = false)
        => CreateClient(null, resolved, ct, invokesTools);

    public async Task<IChatClient> CreateClient(string? brainId, ResolvedModel resolved, CancellationToken ct, bool invokesTools = false)
    {
        var credentials = CredentialsFor(brainId);
        ArgumentNullException.ThrowIfNull(resolved);
        if (!string.Equals(resolved.Revision, Revision(resolved), StringComparison.Ordinal))
        { throw new ArgumentException("The resolved model configuration does not match its revision.", nameof(resolved)); }
        // Pinned options are applied inside the normal pipeline. Validate them before they
        // can overwrite the per-call ceiling at the provider boundary.
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
        return AIClients.BuildChatPipeline(services, !invokesTools || resolved.Capabilities.HasFlag(LlmCapabilities.Tools),
            resolved.Provider.ToString(), pinned, resolved.Provider.ToString(), resolved.Model,
            rejectUnsupportedTools: invokesTools, useFunctionInvocation: invokesTools);
    }

    // One release per build decides whose key this client carries. A brain key goes only to the
    // provider's public endpoint: operator-declared and registration endpoints are the deployment's and
    // never see it. An operator profile with its own endpoint is a deployment model: it always uses the
    // deployment's key and that endpoint, and the brain key is not even released for it.
    private async Task<IChatClient> Build(string? brainId, ResolvedModel resolved, AIOptions configuration, CancellationToken ct)
    {
        var factory = AIClients.Factory(resolved.Provider);
        if (brainId is not null && BrainScope.IsValidId(brainId) && AiIntegrations.KeyedProviders.Contains(resolved.Provider)
            && !HasProfileEndpoint(resolved)
            && await Deployment.BrainKey(brainId, resolved.Provider, ct) is { Length: > 0 } key)
        {
            var publicEndpoint = services.GetRequiredService<ProviderPublicEndpoints>().Of(resolved.Provider);
            return await factory.CreateChatClient(resolved.Model, new AIOptions(), new BrainKeyCredentials(AiIntegrations.IdOf(resolved.Provider), key), publicEndpoint, ct);
        }
        return await factory.CreateChatClient(resolved.Model, configuration, Deployment, resolved.Endpoint, ct);
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
        CapabilitySupport Support(LlmCapabilities capability) => model.Capabilities.HasFlag(capability)
            ? CapabilitySupport.Supported : declared is null ? CapabilitySupport.Unknown : CapabilitySupport.Unsupported;
        return new(model.Provider.ToString(), model.Model, model.Profile, model.Revision,
            Support(LlmCapabilities.Tools), Support(LlmCapabilities.Vision), Support(LlmCapabilities.StructuredOutput),
            profile?.ContextWindowTokens, profile?.MaximumOutputTokens, SupportFlag(profile?.SupportsTemperature),
            SupportFlag(profile?.SupportsTopP), profile?.AllowedReasoning?.ToArray());
    }

    // The browser's allowlist: never a resolved model, which carries operator endpoints.
    // Catalogued models whose provider has no key yet are listed as unavailable, so a picker can show
    // them and say why; profiles that do not resolve are left out.
    public Task<ModelChoices> List(string? defaultModel, CancellationToken ct) => List(null, defaultModel, ct);

    public async Task<ModelChoices> List(string? brainId, string? defaultModel, CancellationToken ct)
    {
        var automatic = await TryResolve(brainId, Default(defaultModel), ct);
        var choices = new List<ModelChoice>();
        foreach (var choice in Choices())
        {
            var (resolved, reason) = await Attempt(brainId, choice.Selection, ct);
            if (resolved is not null) { choices.Add(Describe(choice.Id, choice.Label, resolved)); }
            else if (choice.Preset is { } preset && preset.SupportsTools && reason is not null)
            {
                var presetVisual = VisualCatalog.ForModel(preset.Id, preset.Provider.ToString());
                choices.Add(new(choice.Id, presetVisual.Label, preset.Provider.ToString(), preset.Id, false, [.. Flags(preset.Capabilities)])
                {
                    Reason = reason,
                    IconKey = presetVisual.IconKey,
                    ProviderLabel = presetVisual.Category,
                    AccentHex = presetVisual.AccentHex,
                });
            }
        }
        if (!HasHostChatClient
            && IsDefaultOrLoopbackOllamaEndpoint(options.CurrentValue.Ollama.Endpoint)
            && await DetectLocal(ct).ConfigureAwait(false) is { } local)
        {
            foreach (var tag in local.InstalledModels)
            {
                if (choices.Any(existing => existing.Available && string.Equals(existing.Model, tag, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                var localVisual = VisualCatalog.ForModel(tag, AiProvider.Ollama.ToString());
                choices.Insert(0, new("ollama/" + tag, localVisual.Label, AiProvider.Ollama.ToString(), tag, true, [nameof(LlmCapabilities.Tools)])
                {
                    IconKey = localVisual.IconKey,
                    ProviderLabel = localVisual.Category,
                    AccentHex = localVisual.AccentHex,
                });
            }
        }
        return new(Describe(null, "Automatic (workspace default)", automatic is { Provider: not AiProvider.Host } ? automatic : null), [.. choices]);
    }

    public Task<ModelSelection?> Select(string? id, string? defaultModel, CancellationToken ct) => Select(null, id, defaultModel, ct);

    public async Task<ModelSelection?> Select(string? brainId, string? id, string? defaultModel, CancellationToken ct)
    {
        var selection = ResolveSelection(id, defaultModel);
        const string Unavailable = "The selected model is unavailable or does not support assistant tools. Choose a model from Settings.";
        ResolvedModel resolved;
        try { resolved = await Resolve(brainId, selection, requiresTools: true, ct); }
        catch (ProviderUnavailableException) { throw; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NotSupportedException)
        { throw new ArgumentException(Unavailable); }
        // The host client is a deployment fallback, not a choice a user can make.
        if (resolved.Provider is AiProvider.Host) { throw new ArgumentException(Unavailable); }
        return selection;
    }

    private ModelSelection? ResolveSelection(string? id, string? defaultModel) => id is null ? Default(defaultModel)
        : Choices().FirstOrDefault(choice =>
            string.Equals(choice.Id, id, StringComparison.OrdinalIgnoreCase)
            || string.Equals(choice.Label, id, StringComparison.OrdinalIgnoreCase)
            || string.Equals(choice.Selection.Model, id, StringComparison.OrdinalIgnoreCase)) is { } matched ? matched.Selection
        : ModelRef.TryReadStored(id, options.CurrentValue.ModelProfiles.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase), null, out var parsed)
            ? parsed switch
            {
                ModelRef.Catalogued c => new(c.Model.Marker.Name),
                ModelRef.Profile p => new(p.Name),
                ModelRef.Tag t => new(t.Id),
                _ => throw new ArgumentException("The selected model is unavailable. Choose a model from Settings."),
            }
        : throw new ArgumentException("The selected model is unavailable. Choose a model from Settings.");

    private static ModelSelection? Default(string? defaultModel) => defaultModel is { Length: > 0 } model ? new(model) : null;

    private sealed record Choice(string Id, string Label, ModelSelection Selection, LLMModel? Preset = null);

    // Micros per million input tokens from the module's price list; a local model without a listed
    // price is free, any other unlisted model sorts after every priced one.
    private static long InputPrice(IPriceList? prices, LLMModel model)
        => prices?.MicrosPerUnit(CostKind.Model, model.Id + ":input") ?? (model.Provider == AiProvider.Ollama ? 0 : long.MaxValue);

    private static IEnumerable<string> Flags(LlmCapabilities capabilities)
        => Enum.GetValues<LlmCapabilities>().Where(c => c != LlmCapabilities.None && capabilities.HasFlag(c)).Select(c => c.ToString());

    private IEnumerable<Choice> Choices()
    {
        foreach (var name in options.CurrentValue.ModelProfiles.Keys.Order(StringComparer.OrdinalIgnoreCase))
        { yield return new("profile:" + name, name, new(name)); }
        foreach (var preset in LLMModel.All)
        {
            // Include the configured local default chat model when Ollama endpoint is declared even without explicit model overrides.
            if (preset.Provider == AiProvider.Ollama
                && !options.CurrentValue.Ollama.Models.ContainsKey(preset.Marker.Name)
                && !(preset == DefaultChatModel && !string.IsNullOrWhiteSpace(options.CurrentValue.Ollama.Endpoint)))
            {
                continue;
            }
            var visual = VisualCatalog.ForModel(preset.Id, preset.Provider.ToString());
            yield return new("preset:" + preset.Marker.Name, visual.Label, new(preset.Marker.Name), preset);
        }
    }

    internal const string NoKeyReason = "Add a key in API keys";

    // A catalogued model that does not resolve says why: a key-based provider without a key asks for
    // one; any other refusal carries its own message (catalog messages name no endpoint or secret).
    private async Task<(ResolvedModel? Resolved, string? Reason)> Attempt(string? brainId, ModelSelection selection, CancellationToken ct)
    {
        try { return (await Resolve(brainId, selection, requiresTools: true, ct), null); }
        catch (ProviderUnavailableException) { return (null, NoKeyReason); }
        catch (ArgumentException error) { return (null, error.Message); }
        catch (InvalidOperationException error) { return (null, error.Message); }
        catch (NotSupportedException error) { return (null, error.Message); }
    }

    private async Task<ResolvedModel?> TryResolve(string? brainId, ModelSelection? selection, CancellationToken ct)
    {
        try { return await Resolve(brainId, selection, requiresTools: true, ct); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            services.GetService<ILoggerFactory>()?.CreateLogger<ModelCatalog>()
                ?.LogDebug(error, "Skipping catalog model candidate that cannot resolve.");
            return null;
        }
    }

    private static ModelChoice Describe(string? id, string label, ResolvedModel? model)
    {
        if (model is null)
        {
            return new(id, label, null, null, false, []);
        }

        var visual = VisualCatalog.ForModel(model.Model, model.Provider.ToString());
        return new(id, label, model.Provider.ToString(), model.Model, true, [.. Flags(model.Capabilities)])
        {
            IconKey = visual.IconKey,
            ProviderLabel = visual.Category,
            AccentHex = visual.AccentHex,
        };
    }

    private static string Revision(ResolvedModel resolved)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(resolved with { Revision = string.Empty }))));

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static CapabilitySupport SupportFlag(bool? value)
        => value is null ? CapabilitySupport.Unknown : value.Value ? CapabilitySupport.Supported : CapabilitySupport.Unsupported;

    private static AiProvider ParseProvider(string name)
        => Enum.TryParse<AiProvider>(name, ignoreCase: true, out var provider) && Enum.IsDefined(provider)
            && provider is not (AiProvider.FoundryLocal or AiProvider.Host)
            ? provider : throw new ArgumentException($"Unknown chat provider '{name}'. Use OpenAI, Anthropic, Google, XAI, OpenRouter or Ollama.", nameof(name));

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

    // The host client is a singleton; a caller's `using` must not dispose it.
    private sealed class NonDisposing(IChatClient inner) : DelegatingChatClient(inner)
    {
        protected override void Dispose(bool disposing) { }
    }
}
