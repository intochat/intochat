using System.Text.Json;
using DigitalBrain.AI;
using DigitalBrain.AI.Agents;
using DigitalBrain.Assistant;
using DigitalBrain.Framework.Platform.Integrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace DigitalBrain.Modules.Assistant.Tests;

public sealed class AgentModelsFacts
{
    [Fact]
    public async Task CompletedTurnReplaysWithoutTheRemovedProviderOrProfile()
    {
        await using var brain = await StartAsync(_ => { });
        var completed = new AgentRun("run", AgentRunStatus.Completed, "hello", DateTimeOffset.UtcNow, Reply: "persisted answer");
        var snapshot = new AgentState(2, new(), null, null, [completed]);
        var input = new AssistantRun("thread", "run", "hello", "owner", "profile:removed");

        var prepared = await AssistantTurnExecution.PrepareTurn(snapshot, input, async id => await CatalogOf(brain.SiloServices).Select(id));

        Assert.Equal(completed, prepared.Replay);
        Assert.Null(prepared.Model);
        await Assert.ThrowsAsync<ArgumentException>(() => AssistantTurnExecution.PrepareTurn(snapshot, input with { RunId = "fresh" }, async id => await CatalogOf(brain.SiloServices).Select(id)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => AssistantTurnExecution.PrepareTurn(snapshot,
            input with { Message = "different message" }, async id => await CatalogOf(brain.SiloServices).Select(id)));
        Assert.Null(snapshot.ActiveRun);
        Assert.Single(snapshot.Runs);
    }

    [Fact]
    public async Task CatalogContainsOnlyConfiguredToolModelsAndNeverSecretsOrEndpoints()
    {
        await using var brain = await StartAsync(options =>
        {
            options.ModelProfiles["work"] = new() { Provider = "OpenAI", Model = "private-chat", Capabilities = LlmCapabilities.Tools };
            options.ModelProfiles["no-tools"] = new() { Provider = "OpenAI", Model = "text-only" };
            options.ModelProfiles["unconfigured"] = new() { Provider = "Google", Model = "remote-chat", Capabilities = LlmCapabilities.Tools };
        }, "secret-api-key", "https://private.example/v1");
        var catalog = CatalogOf(brain.SiloServices);
        var result = await catalog.Read();
        Assert.True(result.Automatic.Available);
        Assert.Contains(result.Models, model => model.Id == "profile:work");
        Assert.Contains(result.Models, model => model.Id == "preset:IGpt56Sol");
        Assert.DoesNotContain(result.Models, model => model.Id is "profile:no-tools" or "profile:unconfigured");
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.Select("profile:no-tools"));
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => catalog.Select("profile:unconfigured"));
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("secret-api-key", json);
        Assert.DoesNotContain("private.example", json);
        Assert.DoesNotContain("Endpoint", json);
    }

    [Fact]
    public async Task UnconfiguredDeploymentHasUnavailableAutomaticAndNoChoices()
    {
        await using var brain = await StartAsync(_ => { });
        var catalog = CatalogOf(brain.SiloServices);
        Assert.False((await catalog.Read()).Automatic.Available);
        Assert.Empty((await catalog.Read()).Models);
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.Select(null));
    }

    [Fact]
    public async Task SelectionUsesCatalogIdsAndPreservesAutomaticServerOverride()
    {
        await using var brain = await StartAsync(options =>
        {
            options.ModelProfiles["work"] = new() { Provider = "OpenAI", Model = "private-chat", Capabilities = LlmCapabilities.Tools };
        }, "configured", model: "IGpt56Luna");
        var catalog = CatalogOf(brain.SiloServices);
        Assert.Equal(new ModelSelection("IGpt56Luna"), await catalog.Select(null));
        Assert.Equal(new ModelSelection("work"), await catalog.Select("profile:work"));
        Assert.Equal(new ModelSelection("IGpt56Sol"), await catalog.Select("preset:IGpt56Sol"));
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.Select("https://attacker.example/model"));
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.Select("profile:missing"));
    }

    [Fact]
    public async Task SelectingAnUnavailableModelExplainsWhatIsMissing()
    {
        await using var brain = await StartAsync(_ => { }, "configured");

        var unavailable = await Assert.ThrowsAsync<ProviderUnavailableException>(() => CatalogOf(brain.SiloServices).Select("preset:IGemini31Pro"));

        Assert.Equal("google", unavailable.Integration);
        Assert.Equal("Unconfigured", unavailable.Status);
        Assert.Equal(["ApiKey"], unavailable.Missing);
        Assert.DoesNotContain("configured", unavailable.Message.Replace("Unconfigured", ""));
    }

    [Fact]
    public async Task AnUnavailableProviderIsAnswered409WithTheIntegrationStatusAndMissingFields()
    {
        var http = new global::Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        http.Response.Body = new MemoryStream();

        await AgentEndpoints.ProviderUnavailable(new ProviderUnavailableException("google", "Unconfigured", ["ApiKey"])).ExecuteAsync(http);

        Assert.Equal(409, http.Response.StatusCode);
        http.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(http.Response.Body, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("google", body.RootElement.GetProperty("integration").GetString());
        Assert.Equal("Unconfigured", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("ApiKey", body.RootElement.GetProperty("missing")[0].GetString());
    }

    [Fact]
    public async Task AutomaticWithoutServerOverrideKeepsAiDefault()
    {
        await using var brain = await StartAsync(_ => { }, "configured");
        Assert.Null(await CatalogOf(brain.SiloServices).Select(null));
    }

    private static Task<ModuleBrain> StartAsync(Action<AIOptions> configure, string? apiKey = null, string? endpoint = null, string? model = null)
        => ModuleTest.Create().WithModule<AIModule>().WithModule<AssistantModule>()
            .WithExecution(new TestExecutionOptions
            {
                PrivateConfiguration = apiKey is null ? new Dictionary<string, string?>() : new Dictionary<string, string?>
                {
                    ["Assistant:Model"] = model,
                    [IntegrationSchema.Key("openai", "ApiKey")] = apiKey,
                    [IntegrationSchema.Key("openai", "Endpoint")] = endpoint ?? "https://api.openai.com/v1",
                },
            })
            .ConfigureSilo(silo => silo.Services.Configure(configure))
            .StartAsync(TestContext.Current.CancellationToken);

    private sealed record Catalog(ModelCatalog Models, string? Default)
    {
        public Task<ModelChoices> Read() => Models.List(Default, TestContext.Current.CancellationToken);
        public Task<ModelSelection?> Select(string? id) => Models.Select(id, Default, TestContext.Current.CancellationToken);
    }

    private static Catalog CatalogOf(IServiceProvider services) => new(
        services.GetRequiredService<ModelCatalog>(), services.GetRequiredService<IOptions<AssistantOptions>>().Value.Model);
}
