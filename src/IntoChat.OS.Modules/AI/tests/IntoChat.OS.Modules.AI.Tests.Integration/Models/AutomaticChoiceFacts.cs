using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Connections.Platform.Entries;
using Microsoft.Extensions.DependencyInjection;
using Orleans;

namespace IntoChat.OS.Modules.AI.Tests.Integration.Models;

// Automatic (no selection, no configured default) is the configured tool-capable model with the
// lowest list input price per million tokens, for the calling brain.
public sealed class AutomaticChoiceFacts(AIHost host)
{
    private static ModelCatalog Catalog(ITestBrain brain) => brain.Services.GetRequiredService<ModelCatalog>();

    [Trait("host", "own")]
    [Fact]
    public async Task Automatic_is_cheapest_available_tool_model()
    {
        var ct = TestContext.Current.CancellationToken;
        // Anthropic's first catalogued model is Fable 5 ($5.00/MTok input); its cheapest is Haiku 4.5 ($1.00).
        await using var brain = await TestBrain.Create().WithModule<AIModule>().WithProvider("Anthropic").StartAsync(ct);

        var resolved = await Catalog(brain).Resolve(null, null, true, ct);
        Assert.Equal((AiProvider.Anthropic, "claude-haiku-4-5"), (resolved.Provider, resolved.Model));
        Assert.Equal("claude-haiku-4-5", (await Catalog(brain).List(null, null, ct)).Automatic.Model);

        // A brain that adds an OpenAI key gets GPT-5.4 nano ($0.15), the cheapest across both providers.
        await brain.Services.GetRequiredService<IGrainFactory>().GetGrain<IBrainEntries>("automatic-brain")
            .Put(BrainEntries.ProviderEntry("openai"), EntryKind.Secret, "brain-openai-key-3f19");
        var forBrain = await Catalog(brain).Resolve("automatic-brain", null, true, ct);
        Assert.Equal((AiProvider.OpenAI, "gpt-5.4-nano"), (forBrain.Provider, forBrain.Model));
        Assert.Equal("claude-haiku-4-5", (await Catalog(brain).Resolve("other-brain", null, true, ct)).Model);
    }

    [Fact]
    public async Task Automatic_unavailable_when_none_configured()
    {
        var ct = TestContext.Current.CancellationToken;
        var choices = await Catalog(host.Brain).List("unconfigured-brain", null, ct);
        Assert.False(choices.Automatic.Available);
        Assert.Null(choices.Automatic.Model);
        Assert.All(choices.Models, choice => Assert.False(choice.Available));
        Assert.Equal(AiProvider.Host, (await Catalog(host.Brain).Resolve("unconfigured-brain", null, true, ct)).Provider);
    }
}
