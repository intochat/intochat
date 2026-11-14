using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities.Compute;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Visuals;
using IntoChat.OS.Modules.AI.Inference;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.AI.Models;

// The one resolver from a model key to a client. A key is a marker name, a model ID, or a configured profile
// name; null is the deployment default. brainId is the calling brain: its own provider keys make
// models available to it alone and win over the deployment registration. A null brain sees only the
// deployment's registrations.
public sealed class ModelCatalog(IServiceProvider services, IOptionsMonitor<AIOptions> options)
{
    private IAiCredentials Deployment => services.GetRequiredService<IAiCredentials>();

    private IAiCredentials CredentialsFor(string? brainId)
        => brainId is not null && BrainScope.IsValidId(brainId) && services.GetService<BrainKeyAvailability>() is { } keys
            ? new BrainAwareCredentials(Deployment, keys, brainId)
            : Deployment;

    internal bool HasProfile(string name) => options.CurrentValue.ModelProfiles.ContainsKey(name);

    private bool HasHostChatClient => services.GetService<IChatClient>() is not null and not RebuildingChatClient;

    internal Task<Ollama.LocalOllamaDetection?> DetectLocal(CancellationToken ct)
        => services.GetService<Ollama.ILocalOllamaProbe>() is { } probe
            ? probe.Detect(ct)
            : Task.FromResult<Ollama.LocalOllamaDetection?>(null);

    public Task<ResolvedModel> Resolve(ModelSelection? selection, bool requiresTools, CancellationToken ct)
        => Resolve(null, selection, requiresTools, ct);

    public async Task<ResolvedModel> Resolve(string? brainId, ModelSelection? selection, bool requiresTools, CancellationToken ct)
    {
        var credentials = CredentialsFor(brainId);
        selection ??= new();
        var configuration = options.CurrentValue;
        var useDefault = string.IsNullOrWhiteSpace(selection.Model);
        var key = Clean(selection.Model) ?? Clean(configuration.Default.Model);
        var reasoning = Clean(selection.Reasoning) ?? (useDefault ? Clean(configuration.Default.Reasoning) : null);
        var maxTokens = selection.MaxOutputTokens ?? (useDefault ? configuration.Default.MaxOutputTokens : null);

        if (key is not null && key.StartsWith("profile:", StringComparison.OrdinalIgnoreCase))
        {
            key = Clean(key["profile:".Length..]);
        }

        AiProvider provider;
        string modelId;
        string? endpoint = null;
        string? profileName = null;
        LlmCapabilities? declared = null;
        LLMModel? preset = null;
        if (key is null)
        {
            // Automatic: the configured model with the lowest list input price, among tool-capable
            // models when tools are required. Ties keep catalogue order.
            var prices = services.GetService<IPriceList>();
            foreach (var candidate in LLMModel.All.Where(model => !requiresTools || model.SupportsTools).OrderBy(model => InputPrice(prices, model)))
            {
                if (await AIClients.Factory(candidate.Provider).IsConfigured(configuration, credentials, ct)) { preset = candidate; break; }
            }
            if (preset is null)
            {
                if (!HasHostChatClient
                    && string.IsNullOrWhiteSpace(configuration.Ollama.Endpoint)
                    && await DetectLocal(ct).ConfigureAwait(false) is { PreferredModel.Length: > 0 } localAuto)
                {
                    provider = AiProvider.Ollama;
                    modelId = localAuto.PreferredModel;
                    endpoint = Endpoint(localAuto.Endpoint, AiProvider.Ollama);
                    declared = LlmCapabilities.Tools;
                }
                else
                {
                    var host = new ResolvedModel(AiProvider.Host, "default", null, null, string.Empty, LlmCapabilities.Tools, reasoning, maxTokens);
                    return host with { Revision = Revision(host) };
                }
            }
            else
            {
                provider = preset.Provider;
                modelId = preset.Id;
            }
        }
        else if (TryFindProfile(configuration, key, out var matchedProfileName, out var profile))
        {
            profileName = matchedProfileName;
            provider = ParseProvider(Clean(profile.Provider) ?? throw new ArgumentException($"Model profile '{matchedProfileName}' requires a provider.", nameof(selection)));
            modelId = Clean(profile.Model) ?? throw new ArgumentException($"Model profile '{matchedProfileName}' requires a model ID.", nameof(selection));
            endpoint = Clean(profile.Endpoint);
            reasoning = Clean(selection.Reasoning) ?? Clean(profile.Reasoning);
            maxTokens = selection.MaxOutputTokens ?? profile.MaxOutputTokens;
            declared = profile.Capabilities;
            preset = LLMModel.Find(modelId);
            if (preset is not null && preset.Provider != provider) { preset = null; }
        }
        else if (LLMModel.Find(key) is { } marker)
        {
            preset = marker;
            provider = marker.Provider;
            modelId = marker.Id;
        }
        else if (TryInferModel(key, out var inferredProvider, out var inferredModelId, out var inferredCapabilities))
        {
            provider = inferredProvider;
            modelId = inferredModelId;
            declared = inferredCapabilities;
            preset = LLMModel.Find(modelId);
            if (preset is not null && preset.Provider != provider) { preset = null; }
        }
        else
        {
            throw new ArgumentException($"Unknown model '{key}'. Use a model marker name or a configured profile name.", nameof(selection));
        }

        if (provider == AiProvider.Ollama && preset is not null)
        { modelId = Clean(configuration.Ollama.Models.GetValueOrDefault(preset.Marker.Name)?.Model) ?? modelId; }
        if (modelId.Length > 200 || modelId.Any(char.IsControl))
        { throw new ArgumentException("Model ID must contain at most 200 printable characters.", nameof(selection)); }

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
                        modelId = localDetected.PreferredModel;
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
        { throw new ArgumentException("Model capabilities contain unknown flags.", nameof(selection)); }
        if (requiresTools && !capabilities.HasFlag(LlmCapabilities.Tools))
        { throw new ArgumentException($"Model '{modelId}' does not declare tool support. Configure Tools capabilities in its model profile before attaching tools.", nameof(selection)); }
        reasoning = NormalizeReasoning(reasoning);
        if (requiresTools && provider == AiProvider.OpenAI && modelId.StartsWith("gpt-5.6", StringComparison.OrdinalIgnoreCase))
        {
            if (reasoning is not null and not "none") { throw new ArgumentException("GPT-5.6 tools through OpenAI Chat Completions require reasoning 'none'.", nameof(selection)); }
            reasoning = "none";
        }
        if (maxTokens is < 1 or > 1_000_000) { throw new ArgumentException("MaxOutputTokens must be between 1 and 1000000.", nameof(selection)); }
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

    // Recognizes standard provider-prefixed or family-prefixed model IDs (e.g. "claude-sonnet-4-5",
    // "gpt-5", "gpt-4o", "gemini-2.5-pro", "grok-3", "openrouter/...", "ollama/...", "qwen3.5:2b", "gemma4:e2b").
    private static bool TryInferModel(string key, out AiProvider provider, out string modelId, out LlmCapabilities capabilities)
    {
        capabilities = LlmCapabilities.Tools;
        var trimmed = key.Trim();
        if (trimmed.StartsWith("preset:", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed["preset:".Length..].Trim();
        }

        if (trimmed.StartsWith("ollama/", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("ollama:", StringComparison.OrdinalIgnoreCase))
        {
            var tag = Clean(trimmed[7..]);
            if (tag is not null)
            {
                provider = AiProvider.Ollama;
                modelId = tag;
                return true;
            }
        }
        if (trimmed.StartsWith("openrouter/", StringComparison.OrdinalIgnoreCase))
        {
            var route = Clean(trimmed["openrouter/".Length..]);
            if (route is not null)
            {
                provider = AiProvider.OpenRouter;
                modelId = route;
                return true;
            }
        }
        if (trimmed.StartsWith("anthropic/", StringComparison.OrdinalIgnoreCase) && !trimmed["anthropic/".Length..].Contains('/'))
        {
            var id = Clean(trimmed["anthropic/".Length..]);
            if (id is not null)
            {
                provider = AiProvider.Anthropic;
                modelId = id;
                capabilities = LlmCapabilities.Tools | LlmCapabilities.Vision;
                return true;
            }
        }
        if (trimmed.StartsWith("openai/", StringComparison.OrdinalIgnoreCase) && !trimmed["openai/".Length..].Contains('/'))
        {
            var id = Clean(trimmed["openai/".Length..]);
            if (id is not null)
            {
                provider = AiProvider.OpenAI;
                modelId = id;
                capabilities = LlmCapabilities.Tools | LlmCapabilities.Vision;
                return true;
            }
        }
        if (trimmed.StartsWith("google/", StringComparison.OrdinalIgnoreCase) && !trimmed["google/".Length..].Contains('/'))
        {
            var id = Clean(trimmed["google/".Length..]);
            if (id is not null)
            {
                provider = AiProvider.Google;
                modelId = id;
                capabilities = LlmCapabilities.Tools | LlmCapabilities.Vision;
                return true;
            }
        }
        if (trimmed.StartsWith("xai/", StringComparison.OrdinalIgnoreCase) && !trimmed["xai/".Length..].Contains('/'))
        {
            var id = Clean(trimmed["xai/".Length..]);
            if (id is not null)
            {
                provider = AiProvider.XAI;
                modelId = id;
                return true;
            }
        }

        if (trimmed.StartsWith("claude-", StringComparison.OrdinalIgnoreCase))
        {
            provider = AiProvider.Anthropic;
            modelId = trimmed;
            capabilities = LlmCapabilities.Tools | LlmCapabilities.Vision;
            return true;
        }
        if (trimmed.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("o1-", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("o3-", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("o4-", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "o1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "o3", StringComparison.OrdinalIgnoreCase))
        {
            provider = AiProvider.OpenAI;
            modelId = trimmed;
            capabilities = LlmCapabilities.Tools | LlmCapabilities.Vision;
            return true;
        }
        if (trimmed.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase))
        {
            provider = AiProvider.Google;
            modelId = trimmed;
            capabilities = LlmCapabilities.Tools | LlmCapabilities.Vision;
            return true;
        }
        if (trimmed.StartsWith("grok-", StringComparison.OrdinalIgnoreCase))
        {
            provider = AiProvider.XAI;
            modelId = trimmed;
            return true;
        }
        if (trimmed.Contains('/', StringComparison.Ordinal))
        {
            provider = AiProvider.OpenRouter;
            modelId = trimmed;
            return true;
        }
        if (trimmed.Contains(':', StringComparison.Ordinal)
            || trimmed.StartsWith("qwen", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("gemma", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("llama", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("mistral", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("deepseek", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("phi", StringComparison.OrdinalIgnoreCase))
        {
            provider = AiProvider.Ollama;
            modelId = trimmed;
            return true;
        }

        provider = default;
        modelId = string.Empty;
        return false;
    }

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
            && string.IsNullOrWhiteSpace(options.CurrentValue.Ollama.Endpoint)
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
        : LLMModel.Find(id) is { } preset ? new(preset.Marker.Name)
        : TryFindProfile(options.CurrentValue, id, out var profileName, out _) ? new(profileName)
        : TryInferModel(id, out _, out var inferredId, out _) ? new(inferredId)
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
            // A local server being configured does not imply every compiled local model is installed.
            if (preset.Provider == AiProvider.Ollama && !options.CurrentValue.Ollama.Models.ContainsKey(preset.Marker.Name)) { continue; }
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
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NotSupportedException) { return null; }
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
