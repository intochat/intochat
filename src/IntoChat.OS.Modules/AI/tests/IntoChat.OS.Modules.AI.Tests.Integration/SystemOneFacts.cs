using IntoChat.OS.Modules.AI.SystemOne;
using IntoChat.OS.Connections.Platform.Integrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.AI.Tests.Integration;

// The System One capability speaks TypeSafe's wire shape: state + typed questions in,
// typed answers with probabilities and confidence out.
public sealed class SystemOneFacts
{
    [Fact]
    public async Task AChoiceQuestionIsAnsweredWithAChoiceProbabilitiesAndConfidence()
    {
        var ct = TestContext.Current.CancellationToken;
        using var server = new LoopbackServer();
        var client = Client(server.Url);
        var reply = """
            {"model":"jev-1.13.0","answers":{"department":{"type":"choice","choice":"technical","confidence":0.78,"probabilities":{"technical":0.85,"billing":0.15}}},"usage":{"input_tokens":12,"output_tokens":3}}
            """;
        var captured = server.ReplyOnce("application/json", reply, ct);

        var result = await client.Ask(new SystemOneRequest(
            "the integration keeps failing",
            new() { ["department"] = new SystemOneQuestion.Choice("Which team handles this?", new() { ["technical"] = "Bugs or integration problems", ["billing"] = "Charges and payments" }) }), ct);

        Assert.Equal("jev-1.13.0", result.Model);
        var answer = Assert.IsType<SystemOneAnswer.Choice>(result.Answers["department"]);
        Assert.Equal("technical", answer.Value);
        Assert.Equal(0.78, answer.Confidence);
        Assert.Equal(0.85, answer.Probabilities["technical"]);
        Assert.Equal(12, result.InputTokens);

        var request = await captured;
        Assert.Equal("/v1/systemone", request.Path);
        Assert.Equal("Bearer test-key", request.Authorization);
        Assert.Contains("Which team handles this?", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"choice\"", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANoulQuestionIsAnsweredWithATruthValue()
    {
        var ct = TestContext.Current.CancellationToken;
        using var server = new LoopbackServer();
        var client = Client(server.Url);
        var captured = server.ReplyOnce("application/json",
            """{"model":"jev-1.13.0","answers":{"urgent":{"type":"noul","noul":0.92}}}""", ct);

        var result = await client.Ask(new SystemOneRequest("help ASAP", new() { ["urgent"] = new SystemOneQuestion.Noul("Does this convey urgency?") }), ct);

        var answer = Assert.IsType<SystemOneAnswer.Noul>(result.Answers["urgent"]);
        Assert.Equal(0.92, answer.Value);
        Assert.Contains("\"type\":\"noul\"", (await captured).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDefaultSystemOneMarkerNamesItsCatalogIdOnTheWire()
    {
        var ct = TestContext.Current.CancellationToken;
        using var server = new LoopbackServer();
        var client = Client(server.Url, options => options.Default.SystemOne = "ILocalJev");
        var captured = server.ReplyOnce("application/json",
            """{"model":"llama3.1","answers":{"urgent":{"type":"noul","noul":0.5}}}""", ct);

        await client.Ask(new SystemOneRequest("help", new() { ["urgent"] = new SystemOneQuestion.Noul("Urgent?") }), ct);

        Assert.Contains("\"model\":\"llama3.1\"", (await captured).Body, StringComparison.Ordinal);
    }

    private static HttpSystemOne Client(string endpoint, Action<AIOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddHttpClient();
        services.AddSingleton<IModuleIntegrations<AIModule>>(new Bindings(new Handle(endpoint)));
        var options = new AIOptions { SystemOne = new() { Endpoint = endpoint } };
        configure?.Invoke(options);
        services.AddSingleton(Options.Create(options));
        services.AddSingleton<HttpSystemOne>();
        return services.BuildServiceProvider().GetRequiredService<HttpSystemOne>();
    }

    private sealed class Handle(string endpoint) : IIntegrationHandle
    {
        public string IntegrationId => HttpSystemOne.IntegrationId;

        public Task<IntegrationSnapshot> ReadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Snapshot(endpoint));

        public Task<string> ReadSecretAsync(string field, CancellationToken cancellationToken = default)
            => Task.FromResult("test-key");

        public Task<ReleasedIntegration> ReleaseAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ReleasedIntegration { Snapshot = Snapshot(endpoint), Values = new() { [AiIntegrations.ApiKeyField] = "test-key" } });

        private static IntegrationSnapshot Snapshot(string endpoint) => new()
        {
            IntegrationId = HttpSystemOne.IntegrationId,
            Status = IntegrationStatus.Ready,
            Revision = 1,
            Settings = new() { [AiIntegrations.EndpointField] = endpoint },
        };
    }

    private sealed class Bindings(IIntegrationHandle handle) : IModuleIntegrations<AIModule>
    {
        public IIntegrationHandle Get(string integrationId) => handle;
    }
}
