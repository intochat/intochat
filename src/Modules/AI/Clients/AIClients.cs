using DigitalBrain.Capabilities.Compute;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Modules.AI.Media;
using IntoChat.OS.Modules.AI.Metering;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.AI.Models.Anthropic;
using IntoChat.OS.Modules.AI.Models.Google;
using IntoChat.OS.Modules.AI.Models.Ollama;
using IntoChat.OS.Modules.AI.Models.OpenAI;
using IntoChat.OS.Modules.AI.Models.OpenRouter;
using IntoChat.OS.Modules.AI.Models.XAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.AI;

internal static class AIClients
{
    internal static readonly string ConfigurationRoot = AIKeys.Options;
    private const string TelemetrySource = "IntoChat.OS.Modules.AI";

    private static readonly IReadOnlyDictionary<AiProvider, ILlmProviderFactory> Factories =
        new ILlmProviderFactory[]
        {
            new OpenAIProviderFactory(),
            new AnthropicProviderFactory(),
            new GoogleProviderFactory(),
            new XAIProviderFactory(),
            new OpenRouterProviderFactory(),
            new OllamaProviderFactory(),
        }.ToDictionary(static factory => factory.Provider);

    internal static void Add(IServiceCollection services)
    {
        services.TryAddSingleton<ProviderExecutionGate>();
        foreach (var card in KnownModels.All.Where(static c => c.Can.HasFlag(Abilities.Text) && Factories.ContainsKey(c.Provider)))
        {
            services.AddKeyedSingleton<IChatClient>(
                card.Id,
                (provider, _) => BuildChatPipeline(provider, card));
        }

        foreach (var card in KnownModels.All.Where(static c => c.Can.HasFlag(Abilities.Embed) && Factories.ContainsKey(c.Provider)))
        {
            services.AddKeyedSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
                card.Id,
                (provider, _) =>
                {
                    var inner = new RebuildingEmbeddingGenerator(
                        ct => Factories[card.Provider].CreateEmbeddingGenerator(
                            card, provider.GetRequiredService<IOptions<AIOptions>>().Value, provider.GetRequiredService<IAiCredentials>(), cancellationToken: ct),
                        GenerationOf(provider, card.Provider));
                    IEmbeddingGenerator<string, Embedding<float>> limited = new LimitedEmbeddingGenerator(inner, provider.GetRequiredService<ProviderExecutionGate>());
                    return provider.GetService<ICostMeter>() is { } meter
                        ? new MeteringEmbeddingGenerator(limited, meter, FundingOf(card.Provider), card.Id.Value, provider.GetService<ILoggerFactory>()?.CreateLogger<MeteringEmbeddingGenerator>())
                        : limited;
                });
        }

        // Apply legacy Default:* aliases and local topology defaults before resolving options.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<AIOptions>, LegacyDefaultsAlias>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<AIOptions>, TopologyAiOptionsPostConfigure>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILocalCardSource, OllamaLocalCardSource>());
        services.TryAddSingleton<ModelCatalog>();

        services.TryAddSingleton(provider =>
        {
            var ai = provider.GetRequiredService<IOptions<AIOptions>>().Value;
            var topology = provider.GetService<IConfiguration>()?[IntoChatOSNames.TopologyKey];
            var chat = RoleMaps.Explicit(ai, topology, Role.Chat)?.Value ?? string.Empty;
            return new AIDefaults(chat);
        });
        services.TryAddSingleton(DefaultChatClient);
        services.TryAddSingleton(DefaultEmbeddingGenerator);

        services.AddHostedService<LlmWarmupHostedService>();
    }

    private sealed class TopologyAiOptionsPostConfigure(IServiceProvider services) : IPostConfigureOptions<AIOptions>
    {
        public void PostConfigure(string? name, AIOptions options)
            => ModelCatalog.ApplyTopologyDefaults(options, services.GetService<IConfiguration>());
    }

    internal static ILlmProviderFactory Factory(AiProvider provider)
        => Factories.GetValueOrDefault(provider)
            ?? throw new ArgumentException($"Provider '{provider}' does not support runtime chat model selection.", nameof(provider));

    // What pays for a model call: a deployment integration is billed through the ledger, and a
    // local endpoint (Ollama, Foundry) runs on the host. No AI client is built from a user
    // connection today, so its funding case is never produced here.
    private static Funding FundingOf(AiProvider provider)
        => AiIntegrations.KeyedProviders.Contains(provider) ? Funding.Integration : Funding.Local;

    private static Funding FundingOf(string provider)
        => Enum.TryParse<AiProvider>(provider, out var parsed) ? FundingOf(parsed) : Funding.Local;

    private static IChatClient BuildChatPipeline(IServiceProvider provider, ModelCard card)
        => BuildChatPipeline(provider, card, Rebuilding(provider, card.Provider,
            ct => Factories[card.Provider].CreateChatClient(
                card, provider.GetRequiredService<IOptions<AIOptions>>().Value, provider.GetRequiredService<IAiCredentials>(), cancellationToken: ct)));

    // A keyed provider's client is rebuilt when its registration is re-read, so a cleared or rotated key takes effect without a restart.
    private static IChatClient Rebuilding(IServiceProvider services, AiProvider provider, Func<CancellationToken, Task<IChatClient>> create)
        => new RebuildingChatClient(create, GenerationOf(services, provider));

    private static Func<CancellationToken, Task<long>> GenerationOf(IServiceProvider services, AiProvider provider)
        => ct => AiIntegrations.KeyedProviders.Contains(provider)
            ? services.GetRequiredService<IAiCredentials>().GenerationOf(AiIntegrations.IdOf(provider), ct) : Task.FromResult(0L);

    internal static IChatClient BuildChatPipeline(IServiceProvider provider, ModelCard card, IChatClient innerClient)
        => BuildChatPipeline(provider, card.Can.HasFlag(Abilities.Tools), card.Id.Value, innerClient,
            card.Provider.ToString(), card.Id.Value);

    internal static IChatClient BuildChatPipeline(IServiceProvider provider, bool supportsTools, string telemetryName,
        IChatClient innerClient, string? meterProvider = null, string? meterModel = null,
        bool rejectUnsupportedTools = false, bool useFunctionInvocation = true)
    {
        var configuration = provider.GetRequiredService<IOptions<AIOptions>>().Value;
        var loggerFactory = provider.GetService<ILoggerFactory>();
        var pipeline = new ChatClientBuilder(innerClient);
        if (!supportsTools)
        {
            // Models that cannot emit tool calls must never be told about tools —
            // the assistant then answers capability questions honestly with "no".
            pipeline = pipeline.Use(async (messages, options, next, cancellationToken) =>
            {
                if (options?.Tools is { Count: > 0 })
                {
                    if (rejectUnsupportedTools) { throw new InvalidOperationException("The pinned model does not declare tool support."); }
                    options = options.Clone();
                    options.Tools = null;
                    options.ToolMode = null;
                }

                await next(messages, options, cancellationToken).ConfigureAwait(false);
            });
        }
        // Inference-only clients leave the tool loop to the caller but still need GenAI spans.
        if (useFunctionInvocation) { pipeline = pipeline.UseFunctionInvocation(); }
        pipeline = pipeline.UseOpenTelemetry(
            loggerFactory: loggerFactory,
            sourceName: $"{TelemetrySource}.{telemetryName}",
            configure: telemetry => telemetry.EnableSensitiveData = configuration.Telemetry.EnableSensitiveData ?? false);
        // Innermost: one metered entry per raw provider call, before any function-invocation
        // aggregation, and never a second entry for the same call.
        if (meterProvider is not null && meterModel is not null && provider.GetService<ICostMeter>() is { } meter)
        {
            pipeline = pipeline.Use(inner => new MeteringChatClient(inner, meter, FundingOf(meterProvider), meterModel, loggerFactory?.CreateLogger<MeteringChatClient>()));
        }
        if (provider.GetService<ProviderExecutionGate>() is { } execution)
        { pipeline = pipeline.Use(inner => new LimitedChatClient(inner, execution)); }
        return pipeline.Build(provider);
    }

    private static IChatClient DefaultChatClient(IServiceProvider provider)
        => new RebuildingChatClient(ct => DefaultChatClientAsync(provider, ct), _ => Task.FromResult(0L));

    private static async Task<IChatClient> DefaultChatClientAsync(IServiceProvider provider, CancellationToken cancellationToken)
    {
        var catalog = provider.GetRequiredService<ModelCatalog>();
        var resolved = await catalog.Decide(brainId: null, Role.Chat, ct: cancellationToken);
        if (resolved.Provider is AiProvider.Host)
        {
            throw new NoProviderConfiguredException("No chat provider is configured. Provision a provider integration or configure an Ollama endpoint.");
        }
        return await catalog.CreateClient(resolved, cancellationToken, invokesTools: true);
    }

    private static IEmbeddingGenerator<string, Embedding<float>> DefaultEmbeddingGenerator(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IOptions<AIOptions>>().Value;
        var topology = provider.GetService<IConfiguration>()?[IntoChatOSNames.TopologyKey];
        var configuredId = RoleMaps.Explicit(configuration, topology, Role.Embed);

        if (configuredId is { Value.Length: > 0 } id)
        {
            var configured = KnownModels.Find(id.Value);
            if (configured is null || !configured.Can.HasFlag(Abilities.Embed))
            {
                throw UnknownModel(AIKeys.Roles, id.Value, KnownModels.All.Where(static c => c.Can.HasFlag(Abilities.Embed)).Select(static c => c.Id.Value));
            }
            return provider.GetRequiredKeyedService<IEmbeddingGenerator<string, Embedding<float>>>(configured.Id);
        }

        // The default embedding stays pinned to the local model unless explicitly
        // configured: silently switching it changes vector dimensions and orphans
        // every existing vector collection.
        var local = KnownModels.EmbeddingGemma;
        if (string.IsNullOrEmpty(configuration.Ollama.Endpoint))
        {
            throw new InvalidOperationException(
                $"No embedding model is configured. Supply an Ollama endpoint for {local.Id}, "
                + $"or pin {AIKeys.Role(Where.Cloud, Role.Embed)} to a configured cloud embedding "
                + $"({string.Join(", ", KnownModels.All.Where(static c => c.Can.HasFlag(Abilities.Embed)).Select(static c => c.Id.Value))}).");
        }

        return provider.GetRequiredKeyedService<IEmbeddingGenerator<string, Embedding<float>>>(local.Id);
    }

    internal static void AddImageGeneration(IServiceCollection services, IConfiguration configuration)
        => AddImageGeneration(services, AIOptionsRuntime.Read(configuration), configuration[IntoChatOSNames.TopologyKey]);

    internal static void AddImageGeneration(IServiceCollection services, AIOptions configuration, string? topology = null)
    {
        var configuredId = RoleMaps.Explicit(configuration, topology, Role.Image);
        var card = configuredId is { Value.Length: > 0 } id
            ? KnownModels.Find(id.Value)
            : KnownModels.Find(KnownModels.Defaults.Cloud[Role.Image].Value);

        // Resolution failure is deferred into the factory rather than thrown here:
        // this runs during service registration, so throwing would take the whole
        // silo down over a typo in a peripheral feature.
        if (card is null || !card.Can.HasFlag(Abilities.Image))
        {
            services.AddSingleton<IImageGeneration>(_ => throw UnknownModel(
                AIKeys.Roles, configuredId?.Value ?? string.Empty,
                KnownModels.All.Where(static c => c.Can.HasFlag(Abilities.Image)).Select(static c => c.Id.Value)));
            return;
        }

        services.AddSingleton<IImageGeneration>(sp =>
            new OpenAIImageGeneration(card, sp.GetRequiredService<IAiCredentials>()));
    }

    private static InvalidOperationException UnknownModel(
        string configurationKey,
        string modelId,
        IEnumerable<string> knownIds)
        => new($"{configurationKey} names unknown model '{modelId}'. "
            + $"Known models: {string.Join(", ", knownIds)}.");
}
