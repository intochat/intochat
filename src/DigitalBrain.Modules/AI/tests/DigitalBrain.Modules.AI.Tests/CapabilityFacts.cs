using DigitalBrain.AI;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Integrations;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.AI.Tests;

public sealed class CapabilityFacts
{
    private static readonly CallerContext Caller = new()
    {
        PrincipalId = "owner-1",
        AccountId = "owner-1",
        BrainId = "brain-1",
        Kind = CallerKind.User,
        StampedBy = TrustedEdge.AuthenticatedHttp,
    };

    [Fact]
    public async Task CapabilitiesListOnlyReadyProvidersModels()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create()
            .WithProvider("OpenAI")
            .WithModule<AIModule>().StartAsync(ct);
        var capabilities = brain.Services.GetRequiredService<ICapabilities>();

        var first = await capabilities.List(Caller, "llm", ct);

        Assert.NotEmpty(first);
        Assert.All(first, capability => Assert.Equal("openai", capability.IntegrationId));
        Assert.All(first, capability => Assert.Equal("llm", capability.Kind));

        await brain.Integration("anthropic")
            .Configure(new() { Values = { ["ApiKey"] = "test-only", ["Endpoint"] = "https://api.anthropic.com" } });
        var second = await capabilities.List(Caller, "llm", ct);

        Assert.Contains(second, capability => capability.IntegrationId == "anthropic");
        Assert.Contains(second, capability => capability.IntegrationId == "openai");
        Assert.Empty(await capabilities.List(Caller, "tts", ct));
        Assert.DoesNotContain("test-only", System.Text.Json.JsonSerializer.Serialize(second));
    }
}
