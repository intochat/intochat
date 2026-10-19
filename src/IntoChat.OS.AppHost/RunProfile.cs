using Aspire.Hosting;
using IntoChat.OS.AI;
using IntoChat.OS.AI.Aspire.Hosting;
using IntoChat.OS.AI.Models;
using IntoChat.OS.AI.Models.FoundryLocal;
using IntoChat.OS.AI.Models.Ollama;
using IntoChat.OS.AI.Models.OpenAI;
using IntoChat.OS.AI.Models.OpenRouter;
using IntoChat.OS.ArtCraft;
using IntoChat.OS.ArtCraft.Aspire.Hosting;
using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.ClickHouse;
using IntoChat.OS.ClickHouse.Aspire.Hosting;
using IntoChat.OS.Microsoft.Aspire;
using IntoChat.OS.Microsoft.CSharp;
using IntoChat.OS.Microsoft.GitHub;
using IntoChat.OS.Microsoft.Orleans;
using IntoChat.OS.Microsoft.Playwright;
using IntoChat.OS.Postgres;
using IntoChat.OS.Postgres.Aspire.Hosting;
using IntoChat.OS.Qdrant;
using IntoChat.OS.Qdrant.Aspire.Hosting;
using IntoChat.OS.Supabase;
using IntoChat.OS.Supabase.Aspire.Hosting;

namespace IntoChat.OS.AppHost;

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
    // Only OpenAI is redirected to a local endpoint, so the default chat model must be an OpenAI one: the
    // cloud default (OpenRouter) has no key here and every agent turn without a Model would fail.
    public static BrainProfile Reference => Base() with
    {
        Ai = CloudAi().WithDefaultLlm<IGpt56Luna>().WithoutWebSearch().WithModelEndpoint(AiProvider.OpenAI, new Uri("http://127.0.0.1:1/v1/")),
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

    public static IntoChatOSBuilder Apply(IntoChatOSBuilder brain, BrainProfile profile)
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
        Postgres = new PostgresModuleOptions().WithPostgres(options => options.DatabaseName = "intochat_os"),
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

public static class AppHostIntoChatOS
{
    public static IntoChatOSBuilder Add(IDistributedApplicationBuilder builder, string name, BrainProfile profile, IntoChatOSHostingOptions? hosting = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return RunProfile.Apply(builder.AddIntoChatOS(name, hosting), profile);
    }
}
