using DigitalBrain.AI;
using DigitalBrain.AI.OpenRouter;
using DigitalBrain.Framework.Platform.Integrations;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.AI.Tests;

public sealed class OpenRouterFacts
{
    [Fact]
    public async Task DeepSeekPresetUsesRegisteredOpenRouterCredentialsAndDefaultEndpoint()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithRegistrations(new TestExecutionOptions
        {
            PrivateConfiguration = new Dictionary<string, string?>
            { [IntegrationSchema.Key("openrouter", "ApiKey")] = "test-only" },
        }).WithModule<AIModule>()
            .ConfigureSilo(silo => silo.Services.Configure<AIOptions>(options =>
                options.Default.Model = nameof(IDeepSeekV41Flash))).StartAsync(ct);

        var resolved = await brain.SiloServices.GetRequiredService<ModelProfiles>().Resolve(null, requiresTools: true, cancellationToken: ct);
        Assert.Equal("OpenRouter", resolved.Provider);
        Assert.Equal("deepseek/deepseek-v4.1-flash", resolved.Model);
        Assert.Null(resolved.Endpoint);
        Assert.Equal(IntegrationStatus.Ready,
            (await brain.Get<IIntegration>("integration/openrouter").Read()).Status);
        await brain.Get<IDeepSeekV41Flash>("preset").Describe();
        var defaultModel = await brain.Get<ILLM>("default").Describe();
        Assert.Equal("deepseek/deepseek-v4.1-flash", defaultModel.Model);
    }
}
