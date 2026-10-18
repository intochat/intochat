using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using IntoChat.OS.AI;
using IntoChat.OS.AI.Inference;
using IntoChat.OS.AI.Models;
using IntoChat.OS.AI.Models.OpenAI;
using IntoChat.OS.Framework.Platform.Integrations;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.AI.Tests.Integration;

public sealed class CredentialFacts
{
    private const string Canary = "canary-provider-key-71f0b3";

    [Trait("host", "own")]
    [Fact]
    public async Task AnUnregisteredProviderReportsUnavailableInsteadOfThrowing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<AIModule>().StartAsync(ct);

        var catalogRow = await brain.Integration("openai").Read();
        var factory = new OpenAIProviderFactory();
        var credentials = brain.Services.GetRequiredService<IAiCredentials>();
        var unavailable = await Assert.ThrowsAsync<ProviderUnavailableException>(() => brain.Get<IGpt56Sol>("unregistered").Describe());

        Assert.Equal(IntegrationStatus.Unconfigured, catalogRow.Status);
        Assert.False(await factory.IsConfigured(new AIOptions(), credentials, ct));
        Assert.Equal("openai", unavailable.Integration);
        Assert.Equal("Unconfigured", unavailable.Status);
        Assert.Equal(["ApiKey"], unavailable.Missing);
        Assert.DoesNotContain("Exception", unavailable.Message);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ASeededProviderKeyIsReleasedOnlyInsideTheFactory()
    {
        var ct = TestContext.Current.CancellationToken;
        using var provider = new LoopbackServer();
        await using var brain = await TestBrain.Create()
            .WithProvider("OpenAI", provider.Url, Canary)
            .WithModule<AIModule>()
            .WithOptions<AIModule, AIOptions>(options =>
            {
                options.ModelProfiles["default"] = new() { Provider = "OpenAI", Model = "test-model", Capabilities = LlmCapabilities.Tools };
                options.Default.Model = "default";
            }).StartAsync(ct);
        var registration = brain.Integration("openai");

        var seen = ReplyOnceCapturingKey(provider, ct);
        await brain.Get<ILLM>("default").Generate(new([new("user", [new AiText("hi")])]), cancellationToken: ct);

        Assert.Equal("Bearer " + Canary, await seen);
        var snapshot = await registration.Read();
        Assert.Equal(IntegrationStatus.Ready, snapshot.Status);
        Assert.DoesNotContain(Canary, JsonSerializer.Serialize(snapshot));
        Assert.DoesNotContain(Canary, JsonSerializer.Serialize(await registration.Configure(new() { Values = { ["Endpoint"] = provider.Url } })));
        Assert.DoesNotContain(Canary, JsonSerializer.Serialize(brain.Services.GetRequiredService<IOptions<AIOptions>>().Value));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task SeedingOnlyTheApiKeyIsReadyAndTheDefaultEndpointIsUsed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create()
            .WithProvider("OpenAI", apiKey: Canary)
            .WithModule<AIModule>().StartAsync(ct);

        var snapshot = await brain.Integration("openai").Read();
        var credentials = brain.Services.GetRequiredService<IAiCredentials>();
        using var client = await new OpenAIProviderFactory().CreateChatClient("test-model", new AIOptions(), credentials, cancellationToken: ct);

        Assert.Equal(IntegrationStatus.Ready, snapshot.Status);
        Assert.DoesNotContain("Endpoint", snapshot.Settings.Keys);
        Assert.Equal(AiIntegrations.DefaultEndpointOf(AiProvider.OpenAI), ((ChatClientMetadata)client.GetService(typeof(ChatClientMetadata))!).ProviderUri!.ToString());
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ANewClientUsesTheCurrentEndpointAndKeyDespiteCachedReadiness()
    {
        var ct = TestContext.Current.CancellationToken;
        using var oldProvider = new LoopbackServer();
        using var newProvider = new LoopbackServer();
        await using var brain = await TestBrain.Create().WithProvider("OpenAI", oldProvider.Url, Canary).WithModule<AIModule>().StartAsync(ct);
        var credentials = brain.Services.GetRequiredService<IAiCredentials>();
        await credentials.StatusOf("openai", ct);
        await brain.Integration("openai").Configure(new() { Values = { ["ApiKey"] = "rotated-key", ["Endpoint"] = newProvider.Url } });
        using var client = await new OpenAIProviderFactory().CreateChatClient("test-model", new AIOptions(), credentials, cancellationToken: ct);
        Assert.Equal(new Uri(newProvider.Url), ((ChatClientMetadata)client.GetService(typeof(ChatClientMetadata))!).ProviderUri);
        var seen = ReplyOnceCapturingKey(newProvider, ct);
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: ct);
        Assert.Equal("Bearer rotated-key", await seen);
    }

    [Trait("host", "own")]
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task AClearedOrRotatedRegistrationTakesEffectOnTheLiveClientWithoutARestart(int defaultMode)
    {
        var ct = TestContext.Current.CancellationToken;
        const string Rotated = "canary-rotated-key-2c9e4d";
        var clock = new SteppingTimeProvider();
        using var provider = new LoopbackServer();
        await using var brain = await TestBrain.Create()
            .WithProvider("OpenAI", provider.Url, Canary)
            .WithModule<AIModule>()
            .WithOptions<AIModule, AIOptions>(options =>
            {
                var profile = defaultMode == 2 ? "pinned" : "default";
                options.ModelProfiles[profile] = new() { Provider = "OpenAI", Model = "test-model" };
                options.Default.Model = profile;
            })
            // The credential cache expires on the clock; the fact steps it.
            .ConfigureServices(services => services.AddSingleton<TimeProvider>(clock)).StartAsync(ct);
        var registration = brain.Integration("openai");
        var client = defaultMode != 0 ? brain.Services.GetRequiredService<IChatClient>() : brain.Services.GetRequiredKeyedService<IChatClient>(typeof(IGpt56Sol));
        var hi = new[] { new ChatMessage(ChatRole.User, "hi") };

        var first = ReplyOnceCapturingKey(provider, ct);
        await client.GetResponseAsync(hi, cancellationToken: ct);
        Assert.Equal("Bearer " + Canary, await first);

        await registration.Clear("ApiKey");
        clock.Advance(TimeSpan.FromSeconds(6));
        var unavailable = await Assert.ThrowsAsync<ProviderUnavailableException>(() => client.GetResponseAsync(hi, cancellationToken: ct));
        Assert.Equal(["ApiKey"], unavailable.Missing);

        await registration.Configure(new() { Values = { ["ApiKey"] = Rotated } });
        clock.Advance(TimeSpan.FromSeconds(6));
        var second = ReplyOnceCapturingKey(provider, ct);
        await client.GetResponseAsync(hi, cancellationToken: ct);
        Assert.Equal("Bearer " + Rotated, await second);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task RefreshesWithoutARegistrationChangeNeverInvalidateReleasedKeysButRotationDoes()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new SteppingTimeProvider();
        await using var brain = await TestBrain.Create()
            .WithProvider("OpenAI", apiKey: Canary)
            .WithModule<AIModule>()
            // The credential cache expires on the clock; the fact steps it.
            .ConfigureServices(services => services.AddSingleton<TimeProvider>(clock)).StartAsync(ct);
        var credentials = brain.Services.GetRequiredService<IAiCredentials>();

        var seeded = await credentials.GenerationOf("openai", ct);
        clock.Advance(TimeSpan.FromSeconds(6));
        var refreshed = await credentials.GenerationOf("openai", ct);
        clock.Advance(TimeSpan.FromSeconds(6));
        var refreshedAgain = await credentials.GenerationOf("openai", ct);
        await brain.Integration("openai").Configure(new() { Values = { ["ApiKey"] = "canary-rotated-in-place" } });
        clock.Advance(TimeSpan.FromSeconds(6));

        Assert.Equal(seeded, refreshed);
        Assert.Equal(seeded, refreshedAgain);
        Assert.NotEqual(seeded, await credentials.GenerationOf("openai", ct));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AProviderRefusalNeverLeaksTheKeyIntoErrorsOrLogs()
    {
        var ct = TestContext.Current.CancellationToken;
        var logs = new CapturingLoggerProvider();
        var clock = new SteppingTimeProvider();
        using var provider = new LoopbackServer();
        await using var brain = await TestBrain.Create()
            .WithProvider("OpenAI", provider.Url, Canary)
            .WithModule<AIModule>()
            .WithOptions<AIModule, AIOptions>(options =>
            {
                options.ModelProfiles["default"] = new() { Provider = "OpenAI", Model = "test-model", Capabilities = LlmCapabilities.Tools };
                options.Default.Model = "default";
            })
            // The fact reads every log line the host writes, and steps the credential cache's clock.
            .ConfigureServices(services => services.AddLogging(logging => logging.AddProvider(logs)).AddSingleton<TimeProvider>(clock)).StartAsync(ct);
        var registration = brain.Integration("openai");

        var refused = ReplyOnceCapturingKey(provider, ct, HttpStatusCode.Unauthorized);
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => brain.Get<ILLM>("default").Generate(new([new("user", [new AiText("hi")])]), cancellationToken: ct));
        Assert.Equal("Bearer " + Canary, await refused);
        await registration.Clear("ApiKey");
        clock.Advance(TimeSpan.FromSeconds(6));
        var unavailable = await Assert.ThrowsAsync<ProviderUnavailableException>(() => brain.Get<IGpt56Sol>("cleared").Describe());

        Assert.DoesNotContain(Canary, failure.ToString());
        Assert.DoesNotContain(Canary, unavailable.ToString());
        Assert.DoesNotContain(Canary, JsonSerializer.Serialize(new { unavailable.Integration, unavailable.Status, unavailable.Missing }));
        Assert.NotEmpty(logs.Text);
        Assert.DoesNotContain(Canary, logs.Text);
    }

    private sealed class SteppingTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
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

    private const string ChatCompletionJson = """
        {"id":"r","object":"chat.completion","created":1,"model":"test-model","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
        """;

    private static async Task<string?> ReplyOnceCapturingKey(LoopbackServer provider, CancellationToken cancellationToken, HttpStatusCode status = HttpStatusCode.OK)
        => (await provider.ReplyOnce("application/json", ChatCompletionJson, cancellationToken, status)).Authorization;
}
