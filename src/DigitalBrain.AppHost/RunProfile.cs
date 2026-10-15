using Aspire.Hosting;
using DigitalBrain.AI;
using DigitalBrain.AI.Aspire.Hosting;
using DigitalBrain.AI.Models;
using DigitalBrain.AI.Models.FoundryLocal;
using DigitalBrain.AI.Models.Ollama;
using DigitalBrain.AI.Models.OpenAI;
using DigitalBrain.AI.Models.OpenRouter;
using DigitalBrain.ArtCraft;
using DigitalBrain.ArtCraft.Aspire.Hosting;
using DigitalBrain.Aspire.Hosting;
using DigitalBrain.ClickHouse;
using DigitalBrain.ClickHouse.Aspire.Hosting;
using DigitalBrain.Microsoft.Aspire;
using DigitalBrain.Microsoft.CSharp;
using DigitalBrain.Microsoft.GitHub;
using DigitalBrain.Microsoft.Orleans;
using DigitalBrain.Microsoft.Playwright;
using DigitalBrain.Postgres;
using DigitalBrain.Postgres.Aspire.Hosting;
using DigitalBrain.Qdrant;
using DigitalBrain.Qdrant.Aspire.Hosting;
using DigitalBrain.Supabase;
using DigitalBrain.Supabase.Aspire.Hosting;

namespace DigitalBrain.AppHost;

public sealed record BrainProfile
{
    public required OrleansOptions Orleans { get; init; }
    public required PostgresModuleOptions Postgres { get; init; }
    public required QdrantModuleOptions Qdrant { get; init; }
    public required ClickHouseModuleOptions ClickHouse { get; init; }
    public required SupabaseModuleOptions Supabase { get; init; }
    public required AIOptions Ai { get; init; }
}

public static class RunProfile
{
    public static BrainProfile Local => Base() with { Ai = LocalAi() };
    public static BrainProfile Cloud => Base() with { Ai = CloudAi() };
    public static BrainProfile Reference => Base() with
    {
        Ai = CloudAi().WithoutWebSearch().WithModelEndpoint(AiProvider.OpenAI, new Uri("http://127.0.0.1:1/v1/")),
    };

    public static void CopyOnto(AIOptions target, AIOptions source)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);
        target.Default = source.Default;
        target.Telemetry = source.Telemetry;
        target.OpenAI = source.OpenAI;
        target.Anthropic = source.Anthropic;
        target.Google = source.Google;
        target.XAI = source.XAI;
        target.OpenRouter = source.OpenRouter;
        target.Ollama = source.Ollama;
        target.Tavily = source.Tavily;
        target.SystemOne = source.SystemOne;
        target.Hosting = source.Hosting;
        target.ModelProfiles.Clear();
        foreach (var (key, value) in source.ModelProfiles)
        { target.ModelProfiles[key] = value; }
    }

    public static DigitalBrainBuilder Apply(DigitalBrainBuilder brain, BrainProfile profile)
    {
        ArgumentNullException.ThrowIfNull(brain);
        ArgumentNullException.ThrowIfNull(profile);
        return brain
            .WithHosting<OrleansModuleHosting, OrleansOptions>(profile.Orleans)
            .WithHosting<PostgresModuleHosting, PostgresModuleOptions>(profile.Postgres)
            .WithHosting<QdrantModuleHosting, QdrantModuleOptions>(profile.Qdrant)
            .WithHosting<ClickHouseModuleHosting, ClickHouseModuleOptions>(profile.ClickHouse)
            .WithHosting<SupabaseModuleHosting, SupabaseModuleOptions>(profile.Supabase)
            .WithHosting<AIModuleHosting, AIOptions>(profile.Ai)
            .WithHosting<GitHubModuleHosting>()
            .WithHosting<AspireModuleHosting>()
            .WithHosting<CSharpModuleHosting>()
            .WithHosting<ArtCraftModuleHosting, ArtCraftModuleOptions>(artcraft => artcraft.WithHostedApps())
            .WithModule<PlaywrightModule, PlaywrightModuleOptions>(playwright => playwright.AppOrigins = ArtCraftModuleOptions.HostedOrigins);
    }

    private static BrainProfile Base() => new()
    {
        Orleans = new OrleansOptions().WithAzurite(),
        Postgres = new PostgresModuleOptions().WithPostgres(options => options.DatabaseName = "digitalbrain"),
        Qdrant = new QdrantModuleOptions().WithHostedQdrant(),
        ClickHouse = new ClickHouseModuleOptions().WithClickHouse(),
        Supabase = new SupabaseModuleOptions().WithConnection("supabase"),
        Ai = new AIOptions(),
    };

    private static AIOptions LocalAi() => new AIOptions()
        .WithDefaultLlm<IQwen35>()
        .WithDefaultEmbedding<IEmbeddingGemma>()
        .WithVoiceToText<IWhisperSmall>()
        .WithDefaultSystemOne<ILocalJev>()
        .WithoutWebSearch();

    private static AIOptions CloudAi() => new AIOptions()
        .WithLlm<IGpt56Luna>()
        .WithDefaultLlm<IDeepSeekV41Flash>()
        .WithDefaultEmbedding<ITextEmbedding3Small>()
        .WithVoiceToText<IWhisperLargeV3Turbo>()
        .WithoutLocalModels()
        .WithTavilySearch();
}

public static class AppHostDigitalBrain
{
    public static DigitalBrainBuilder Add(IDistributedApplicationBuilder builder, string name, BrainProfile profile, DigitalBrainHostingOptions? hosting = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return RunProfile.Apply(builder.AddDigitalBrain(name, hosting), profile);
    }
}
