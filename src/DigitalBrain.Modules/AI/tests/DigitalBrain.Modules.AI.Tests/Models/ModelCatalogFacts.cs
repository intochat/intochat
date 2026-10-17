using DigitalBrain.AI;
using DigitalBrain.AI.Models;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DigitalBrain.Modules.AI.Tests.Models;

public sealed class ModelCatalogFacts
{
    private static ModelCatalog Catalog(ITestBrain brain) => brain.Services.GetRequiredService<ModelCatalog>();

    [Fact]
    public async Task AMarkerNameResolvesToItsProviderWhenCredentialsAreReady()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<AIModule>().WithProvider("OpenAI").StartAsync(ct);
        var resolved = await Catalog(brain).Resolve(new("IGpt56Sol"), requiresTools: true, ct);
        Assert.Equal((AiProvider.OpenAI, "gpt-5.6-sol"), (resolved.Provider, resolved.Model));
        Assert.True(resolved.Capabilities.HasFlag(LlmCapabilities.Tools));
        Assert.NotEmpty(resolved.Revision);
    }

    [Fact]
    public async Task AProfileNameResolvesToTheProfilesProviderModelAndEndpoint()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<AIModule, AIOptions>(options =>
            options.ModelProfiles["work"] = new() { Provider = "OpenAI", Model = "private-chat", Endpoint = "https://private.example/v1", Capabilities = LlmCapabilities.Tools })
            .WithProvider("OpenAI").StartAsync(ct);
        var resolved = await Catalog(brain).Resolve(new("work"), requiresTools: true, ct);
        Assert.Equal((AiProvider.OpenAI, "private-chat", "https://private.example/v1", "work"), (resolved.Provider, resolved.Model, resolved.Endpoint, resolved.Profile));
    }

    [Fact]
    public async Task NoSelectionAndNoConfigurationResolvesToTheHostClient()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<AIModule>().StartAsync(ct);
        var resolved = await Catalog(brain).Resolve(null, requiresTools: false, ct);
        Assert.Equal(AiProvider.Host, resolved.Provider);
        Assert.False((await Catalog(brain).List(null, ct)).Automatic.Available);
    }

    [Fact]
    public async Task UnknownKeysAndUnconfiguredProvidersAreRefusedBeforeAnyProviderCall()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<AIModule, AIOptions>(options =>
            options.ModelProfiles["remote"] = new() { Provider = "Google", Model = "remote-chat", Capabilities = LlmCapabilities.Tools }).StartAsync(ct);
        await Assert.ThrowsAsync<ArgumentException>(() => Catalog(brain).Resolve(new("INoSuchModel"), false, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Catalog(brain).Resolve(new("https://attacker.example/model"), false, ct));
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => Catalog(brain).Resolve(new("remote"), true, ct));
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => Catalog(brain).Resolve(new("IFable5"), true, ct));
    }

    [Fact]
    public async Task ListOffersOnlyConfiguredToolModelsAndSelectValidatesChoiceIds()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<AIModule, AIOptions>(options =>
            {
                options.ModelProfiles["work"] = new() { Provider = "OpenAI", Model = "private-chat", Capabilities = LlmCapabilities.Tools };
                options.ModelProfiles["no-tools"] = new() { Provider = "OpenAI", Model = "text-only", Capabilities = LlmCapabilities.None };
            }).WithProvider("OpenAI", "https://private.example/v1", "secret-api-key").StartAsync(ct);
        var catalog = brain.Get<IModelCatalog>("default");
        var choices = await catalog.List();
        Assert.True(choices.Automatic.Available);
        Assert.Contains(choices.Models, choice => choice.Id == "profile:work");
        Assert.Contains(choices.Models, choice => choice.Id == "preset:IGpt56Sol");
        Assert.DoesNotContain(choices.Models, choice => choice.Id == "profile:no-tools");
        var json = System.Text.Json.JsonSerializer.Serialize(choices);
        Assert.DoesNotContain("secret-api-key", json);
        Assert.DoesNotContain("private.example", json);
        Assert.Equal(new ModelSelection("work"), await catalog.Select("profile:work"));
        Assert.Equal(new ModelSelection("IGpt56Sol"), await catalog.Select("preset:IGpt56Sol"));
        Assert.Equal(new ModelSelection("IGpt56Luna"), await catalog.Select(null, "IGpt56Luna"));
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.Select("profile:no-tools"));
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.Select("profile:missing"));
    }
}
