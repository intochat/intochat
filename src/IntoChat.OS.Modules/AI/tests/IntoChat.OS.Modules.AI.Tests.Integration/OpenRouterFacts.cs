using IntoChat.OS.AI;
using IntoChat.OS.AI.Models;
using IntoChat.OS.AI.Models.OpenRouter;
using IntoChat.OS.Framework.Platform.Integrations;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.AI.Tests.Integration;

public sealed class OpenRouterFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task DeepSeekPresetUsesRegisteredOpenRouterCredentialsAndDefaultEndpoint()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithProvider("OpenRouter").WithModule<AIModule>()
            .WithOptions<AIModule, AIOptions>(options =>
                options.Default.Model = nameof(IDeepSeekV41Flash)).StartAsync(ct);

        var resolved = await brain.Services.GetRequiredService<ModelCatalog>().Resolve(null, requiresTools: true, ct);
        Assert.Equal(AiProvider.OpenRouter, resolved.Provider);
        Assert.Equal("deepseek/deepseek-v4.1-flash", resolved.Model);
        Assert.Null(resolved.Endpoint);
        Assert.Equal(IntegrationStatus.Ready,
            (await brain.Integration("openrouter").Read()).Status);
        await brain.Get<IDeepSeekV41Flash>("preset").Describe();
        var defaultModel = await brain.Get<ILLM>("default").Describe();
        Assert.Equal("deepseek/deepseek-v4.1-flash", defaultModel.Model);
    }
}
