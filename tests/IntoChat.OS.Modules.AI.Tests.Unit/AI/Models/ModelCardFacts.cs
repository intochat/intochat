using System.Text.Json;
using IntoChat.OS.Modules.AI.Models;

namespace IntoChat.OS.Tests.Unit.Modules.AI.Models;

public sealed class ModelCardFacts
{
    [Fact]
    public void ModelIdIsABareJsonString()
    {
        var json = JsonSerializer.Serialize(new ModelId("qwen3.5:4b"), AiContractsJson.Default.ModelId);
        Assert.Equal("\"qwen3.5:4b\"", json);
        Assert.Equal(new ModelId("qwen3.5:4b"), JsonSerializer.Deserialize(json, AiContractsJson.Default.ModelId));
    }

    [Fact]
    public void ExistingProviderNumbersNeverMove()
    {
        Assert.Equal(0, (int)AiProvider.OpenAI);
        Assert.Equal(1, (int)AiProvider.Anthropic);
        Assert.Equal(2, (int)AiProvider.Google);
        Assert.Equal(3, (int)AiProvider.XAI);
        Assert.Equal(4, (int)AiProvider.Ollama);
        Assert.Equal(5, (int)AiProvider.FoundryLocal);
        Assert.Equal(6, (int)AiProvider.OpenRouter);
        Assert.Equal(7, (int)AiProvider.TypeSafe);
        Assert.Equal(8, (int)AiProvider.Host);
        Assert.Equal(9, (int)AiProvider.OpenAICompatible);
        Assert.Equal(10, (int)AiProvider.SherpaOnnx);
        Assert.Equal(11, (int)AiProvider.Device);
    }

    [Theory]
    [InlineData(AiProvider.OpenAI, "provider:openai")]
    [InlineData(AiProvider.Anthropic, "provider:anthropic")]
    [InlineData(AiProvider.Google, "provider:google")]
    [InlineData(AiProvider.XAI, "provider:xai")]
    [InlineData(AiProvider.Ollama, "provider:ollama")]
    [InlineData(AiProvider.FoundryLocal, "provider:foundry")]
    [InlineData(AiProvider.OpenRouter, "provider:openrouter")]
    [InlineData(AiProvider.TypeSafe, "brand:intochat")]
    [InlineData(AiProvider.Host, "brand:intochat")]
    [InlineData(AiProvider.OpenAICompatible, "provider:openaicompatible")]
    [InlineData(AiProvider.SherpaOnnx, "provider:sherpaonnx")]
    public void IconKeyMatchesCanonicalProviderVocabulary(AiProvider provider, string key) =>
        Assert.Equal(key, new ModelCard(new("x"), provider, "F", "N", Where.Device, Abilities.Text, Tier.Fast).IconKey());

    [Theory]
    [InlineData(ModelRole.Chat, Abilities.Text)]
    [InlineData(ModelRole.Think, Abilities.Text)]
    [InlineData(ModelRole.Fast, Abilities.Text)]
    [InlineData(ModelRole.Hear, Abilities.Hear)]
    [InlineData(ModelRole.Speak, Abilities.Speak)]
    [InlineData(ModelRole.Talk, Abilities.Talk)]
    [InlineData(ModelRole.Embed, Abilities.Embed)]
    [InlineData(ModelRole.Image, Abilities.Image)]
    public void RoleNeedsMatchesEachRole(ModelRole role, Abilities expected) =>
        Assert.Equal(expected, RoleNeeds.Of(role));

    [Fact]
    public void RoleMapJsonUsesRoleNames()
    {
        var map = RoleMap.Empty with { Device = RoleMap.Empty.Device.Add(ModelRole.Chat, new("qwen3.5:4b")) };
        var json = JsonSerializer.Serialize(map, AiContractsJson.Default.RoleMap);
        Assert.Contains("\"Chat\":\"qwen3.5:4b\"", json, StringComparison.Ordinal);
        var roundTripped = JsonSerializer.Deserialize(json, AiContractsJson.Default.RoleMap);
        Assert.NotNull(roundTripped);
        Assert.Equal(new ModelId("qwen3.5:4b"), roundTripped.Device[ModelRole.Chat]);
    }

    [Fact]
    public void ModelCardJsonRoundTripsWithOptionalPriceAndBytes()
    {
        var card = new ModelCard(
            new("gpt-5.6-sol"),
            AiProvider.OpenAI,
            "GPT-5.6",
            "GPT-5.6 Sol",
            Where.Cloud,
            Abilities.Text | Abilities.Tools | Abilities.Vision,
            Tier.Deep,
            new ModelPrice(5.00m, 20.00m, CachedInputPerMTok: 2.50m, ReasoningPerMTok: 20.00m),
            Bytes: null);

        var json = JsonSerializer.Serialize(card, AiContractsJson.Default.ModelCard);
        var back = JsonSerializer.Deserialize(json, AiContractsJson.Default.ModelCard);
        Assert.Equal(card, back);
    }
}
