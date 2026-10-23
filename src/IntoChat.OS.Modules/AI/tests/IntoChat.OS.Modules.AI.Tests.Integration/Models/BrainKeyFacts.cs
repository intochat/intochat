using System.Net;
using IntoChat.OS.AI;
using IntoChat.OS.AI.Models;
using IntoChat.OS.Framework.Platform.Entries;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.AI.Tests.Integration.Models;

// A brain's own provider key (the provider:{id} secret entry) makes models available to that brain
// alone, wins over the deployment registration for that brain, and falls back when removed.
public sealed class BrainKeyFacts
{
    private const string BrainA = "brain-key-a";
    private const string BrainB = "brain-key-b";
    private const string Canary = "canary-brain-key-5d81e0";
    private const string DeploymentKey = "deployment-key-0a77c2";

    private static ModelCatalog Catalog(ITestBrain brain) => brain.Services.GetRequiredService<ModelCatalog>();

    private static IBrainEntries Entries(ITestBrain brain, string brainId)
        => brain.Services.GetRequiredService<IGrainFactory>().GetGrain<IBrainEntries>(brainId);

    [Trait("host", "own")]
    [Fact]
    public async Task Brain_key_flips_availability_only_for_that_brain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<AIModule>().StartAsync(ct);

        var before = await Catalog(brain).List(BrainA, null, ct);
        Assert.False(before.Automatic.Available);
        Assert.False(Assert.Single(before.Models, choice => choice.Id == "preset:IGpt54Nano").Available);

        await Entries(brain, BrainA).Put(BrainEntries.ProviderEntry("openai"), EntryKind.Secret, Canary);

        var a = await Catalog(brain).List(BrainA, null, ct);
        var b = await Catalog(brain).List(BrainB, null, ct);
        Assert.True(a.Automatic.Available);
        Assert.True(Assert.Single(a.Models, choice => choice.Id == "preset:IGpt54Nano").Available);
        Assert.False(b.Automatic.Available);
        Assert.False(Assert.Single(b.Models, choice => choice.Id == "preset:IGpt54Nano").Available);
        Assert.DoesNotContain(Canary, System.Text.Json.JsonSerializer.Serialize(a));
        await Catalog(brain).Select(BrainA, "preset:IGpt54Nano", null, ct);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => Catalog(brain).Select(BrainB, "preset:IGpt54Nano", null, ct));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task Brain_key_wins_over_deployment_registration()
    {
        var ct = TestContext.Current.CancellationToken;
        var logs = new CapturingLogs();
        using var servers = new Servers();
        await using var brain = await Host(servers, logs, ct);
        await Entries(brain, BrainA).Put(BrainEntries.ProviderEntry("openai"), EntryKind.Secret, Canary);

        // The brain's key goes to the provider's public endpoint; other brains keep the operator's.
        Assert.Equal("Bearer " + Canary, await Call(brain, servers.Public, BrainA, "work", ct));
        Assert.Equal("Bearer " + DeploymentKey, await Call(brain, servers.Declared, BrainB, "work", ct));
        Assert.Equal("Bearer " + DeploymentKey, await Call(brain, servers.Declared, null, "work", ct));
        Assert.NotEmpty(logs.Text);
        Assert.DoesNotContain(Canary, logs.Text);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task Brain_key_never_reaches_operator_or_profile_endpoint()
    {
        var ct = TestContext.Current.CancellationToken;
        using var servers = new Servers();
        await using var brain = await Host(servers, new CapturingLogs(), ct);
        await Entries(brain, BrainA).Put(BrainEntries.ProviderEntry("openai"), EntryKind.Secret, Canary);

        // A profile's pinned endpoint, the declared AIOptions endpoint and the registration endpoint
        // are the operator's: with the brain's key the call goes to the public endpoint instead.
        Assert.Equal("Bearer " + Canary, await Call(brain, servers.Public, BrainA, "pinned", ct));
        Assert.Equal("Bearer " + Canary, await Call(brain, servers.Public, BrainA, "work", ct));
        await NoRequest(servers.Profile, ct);
        await NoRequest(servers.Declared, ct);
        await NoRequest(servers.Registered, ct);
        // The deployment's own key still honours the pinned endpoint.
        Assert.Equal("Bearer " + DeploymentKey, await Call(brain, servers.Profile, BrainB, "pinned", ct));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task Removing_brain_key_falls_back_to_deployment()
    {
        var ct = TestContext.Current.CancellationToken;
        using var servers = new Servers();
        await using var brain = await Host(servers, new CapturingLogs(), ct);
        await Entries(brain, BrainA).Put(BrainEntries.ProviderEntry("openai"), EntryKind.Secret, Canary);
        Assert.Equal("Bearer " + Canary, await Call(brain, servers.Public, BrainA, "work", ct));

        await Entries(brain, BrainA).Remove(BrainEntries.ProviderEntry("openai"));

        Assert.Equal("Bearer " + DeploymentKey, await Call(brain, servers.Declared, BrainA, "work", ct));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task Removing_brain_key_makes_brain_only_models_unavailable_again()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<AIModule>().StartAsync(ct);
        await Entries(brain, BrainA).Put(BrainEntries.ProviderEntry("anthropic"), EntryKind.Secret, Canary);
        Assert.True((await Catalog(brain).List(BrainA, null, ct)).Automatic.Available);

        await Entries(brain, BrainA).Remove(BrainEntries.ProviderEntry("anthropic"));

        Assert.False((await Catalog(brain).List(BrainA, null, ct)).Automatic.Available);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task Unavailable_reason_asks_for_a_key_only_when_the_provider_has_none()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<AIModule>().StartAsync(ct);
        var choices = await Catalog(brain).List(BrainA, null, ct);
        var nano = Assert.Single(choices.Models, choice => choice.Id == "preset:IGpt54Nano");
        Assert.Equal("Add a key in API keys", nano.Reason);
        Assert.All(choices.Models.Where(choice => choice.Available), choice => Assert.Null(choice.Reason));
    }

    // The provider's public endpoint, the registration's, the declared AIOptions endpoint and a
    // profile's pinned endpoint, each a separate loopback server.
    private sealed class Servers : IDisposable
    {
        public LoopbackServer Public { get; } = new();
        public LoopbackServer Registered { get; } = new();
        public LoopbackServer Declared { get; } = new();
        public LoopbackServer Profile { get; } = new();

        public void Dispose()
        {
            Public.Dispose();
            Registered.Dispose();
            Declared.Dispose();
            Profile.Dispose();
        }
    }

    private sealed class LoopbackPublic(string url) : ProviderPublicEndpoints
    {
        public override string Of(AiProvider provider) => url;
    }

    private static Task<ITestBrain> Host(Servers servers, CapturingLogs logs, CancellationToken ct)
        => TestBrain.Create()
            .WithProvider("OpenAI", servers.Registered.Url, DeploymentKey)
            .WithModule<AIModule>()
            .WithOptions<AIModule, AIOptions>(options =>
            {
                options.Provider(AiProvider.OpenAI).Endpoint = servers.Declared.Url;
                options.ModelProfiles["work"] = new() { Provider = "OpenAI", Model = "test-model", Capabilities = LlmCapabilities.Tools };
                options.ModelProfiles["pinned"] = new() { Provider = "OpenAI", Model = "test-model", Endpoint = servers.Profile.Url, Capabilities = LlmCapabilities.Tools };
            })
            .ConfigureServices(services => services
                .AddLogging(logging => logging.AddProvider(logs).SetMinimumLevel(LogLevel.Trace))
                .AddSingleton<ProviderPublicEndpoints>(new LoopbackPublic(servers.Public.Url)))
            .StartAsync(ct);

    private static async Task<string?> Call(ITestBrain brain, LoopbackServer expected, string? brainId, string profile, CancellationToken ct)
    {
        var catalog = Catalog(brain);
        var resolved = await catalog.Resolve(brainId, new(profile), true, ct);
        using var client = await catalog.CreateClient(brainId, resolved, ct);
        var seen = expected.ReplyOnce("application/json", ChatCompletionJson, ct, HttpStatusCode.OK);
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: ct);
        return (await seen).Authorization;
    }

    private static async Task NoRequest(LoopbackServer server, CancellationToken ct)
    {
        using var quiet = CancellationTokenSource.CreateLinkedTokenSource(ct);
        quiet.CancelAfter(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => server.ReplyOnce("application/json", ChatCompletionJson, quiet.Token));
    }

    private const string ChatCompletionJson = """
        {"id":"r","object":"chat.completion","created":1,"model":"test-model","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
        """;

    private sealed class CapturingLogs : ILoggerProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new();

        public string Text => string.Join(Environment.NewLine, _lines);

        public ILogger CreateLogger(string categoryName) => new Sink(_lines);

        public void Dispose() { }

        private sealed class Sink(System.Collections.Concurrent.ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => lines.Enqueue(formatter(state, exception) + exception);
        }
    }
}
