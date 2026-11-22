using System.Collections.Immutable;
using DigitalBrain.Capabilities.Compute;
using IntoChat.OS.Modules.AI.Generators;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.AI.Pricing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace IntoChat.OS.Tests.Unit.Modules.AI.Models;

public sealed class ModelsFacts
{
    // Copied from LLMModel.All, EmbeddingModel.All, TranscriptionModel.All, ImageModel.All,
    // and SystemOneModel.All before deletion so every retired marker and wire id keeps resolving.
    public static TheoryData<string, string> RetiredMarkers => new()
    {
        // LLMModel.All (19)
        { "IGpt56Sol", "gpt-5.6-sol" },
        { "IGpt56Terra", "gpt-5.6-terra" },
        { "IGpt56Luna", "gpt-5.6-luna" },
        { "IGpt54", "gpt-5.4" },
        { "IGpt54Mini", "gpt-5.4-mini" },
        { "IGpt54Nano", "gpt-5.4-nano" },
        { "IFable5", "claude-fable-5" },
        { "IOpus5", "claude-opus-5" },
        { "IOpus46", "claude-opus-4-6" },
        { "ISonnet5", "claude-sonnet-5" },
        { "ISonnet46", "claude-sonnet-4-6" },
        { "IHaiku45", "claude-haiku-4-5" },
        { "IGemini31Pro", "gemini-3.1-pro-preview" },
        { "IGemini36Pro", "gemini-3.6-pro" },
        { "IGemini36Flash", "gemini-3.6-flash" },
        { "IGrok46", "grok-4.6" },
        { "IDeepSeekV41Flash", "deepseek/deepseek-v4.1-flash" },
        { "IGemma4", "gemma4:e2b" },
        { "IQwen35", "qwen3.5:4b" },
        // EmbeddingModel.All (4)
        { "ITextEmbedding3Small", "text-embedding-3-small" },
        { "ITextEmbedding3Large", "text-embedding-3-large" },
        { "IGeminiEmbedding", "gemini-embedding-001" },
        { "IEmbeddingGemma", "embeddinggemma" },
        // TranscriptionModel.All (6)
        { "IGpt4oMiniTranscribe", "gpt-4o-mini-transcribe" },
        { "IGpt4oTranscribe", "gpt-4o-transcribe" },
        { "IWhisper1", "whisper-1" },
        { "IWhisperLargeV3Turbo", "whisper-large-v3-turbo" },
        { "IWhisperSmall", "whisper-small" },
        { "IWhisperTiny", "whisper-tiny" },
        // ImageModel.All (1)
        { "IGptImage1", "gpt-image-1" },
        // SystemOneModel.All (2)
        { "IJev", "jev-latest" },
        { "ILocalJev", "llama3.1" },
    };

    [Theory, MemberData(nameof(RetiredMarkers))]
    public void EveryRetiredMarkerStillResolves(string marker, string id)
    {
        Assert.Equal(id, KnownModels.Find(marker)!.Id.Value);
        Assert.Equal(id, KnownModels.Find("preset:" + marker)!.Id.Value);
        Assert.Equal(id, KnownModels.Find(id)!.Id.Value);
    }

    [Theory]
    [InlineData("gemma4-12b", "gemma4:12b")]
    [InlineData("sonnet-4.6", "claude-sonnet-4-6")]
    [InlineData("opus-4.6", "claude-opus-4-6")]
    [InlineData("gemini-3.1-pro", "gemini-3.1-pro-preview")]
    [InlineData("qwen2.5-coder", "qwen2.5-coder:14b")]
    public void LegacyAliasesResolveToCanonicalCard(string alias, string expectedId) =>
        Assert.Equal(expectedId, KnownModels.Find(alias)!.Id.Value);

    [Fact]
    public void IdsAreUnique() =>
        Assert.Equal(KnownModels.All.Count, KnownModels.All.Select(static m => m.Id).Distinct().Count());

    [Fact]
    public void EveryDefaultIsACardWithTheRolesAbilities()
    {
        foreach (var (role, id) in KnownModels.Defaults.Device.Concat(KnownModels.Defaults.Cloud))
        {
            var card = KnownModels.Find(id.Value);
            Assert.NotNull(card);
            Assert.True(card.Can.HasFlag(RoleNeeds.Of(role)), $"{role} -> {id}");
        }
    }

    [Fact]
    public void DeviceDefaultsAreDeviceCards() =>
        Assert.All(KnownModels.Defaults.Device.Values, id => Assert.Equal(Where.Device, KnownModels.Find(id.Value)!.Where));

    [Fact]
    public void PricesComeFromTheCards()
    {
        var prices = new ModelPriceList();
        Assert.Equal(5_000_000L, prices.MicrosPerUnit(CostKind.Model, "gpt-5.6-sol:input"));
        Assert.Equal(2_500_000L, prices.MicrosPerUnit(CostKind.Model, "gpt-5.6-sol:cached"));
        Assert.Equal(20_000_000L, prices.MicrosPerUnit(CostKind.Model, "gpt-5.6-sol:reasoning"));
        Assert.Equal(20_000_000L, prices.MicrosPerUnit(CostKind.Model, "gpt-5.6-sol:output"));
        Assert.Equal(0L, prices.MicrosPerUnit(CostKind.Model, "qwen3.5:4b:input"));
        Assert.Null(prices.MicrosPerUnit(CostKind.Model, "unknown-model:input"));
    }

    [Fact]
    public void UnknownIdsAreNotGuessed()
    {
        Assert.Null(KnownModels.Find("claude-made-up"));
        Assert.Null(KnownModels.Find("gpt-9"));
        Assert.Null(KnownModels.Find(null));
        Assert.Null(KnownModels.Find("   "));
    }

    [Fact]
    public void KokoroIsADeviceSpeakCard() =>
        Assert.Equal(
            (Where.Device, Abilities.Speak, AiProvider.SherpaOnnx),
            (KnownModels.Kokoro82M.Where, KnownModels.Kokoro82M.Can, KnownModels.Kokoro82M.Provider));

    [Fact]
    public void TheCloudTalksThroughGptRealtime() =>
        Assert.Equal(KnownModels.GptRealtime.Id, KnownModels.Defaults.Cloud[Role.Talk]);

    [Fact]
    public void TheDeviceHasNoTalkCard() =>
        Assert.False(KnownModels.Defaults.Device.ContainsKey(Role.Talk));

    [Fact]
    public void ProviderNumbersNeverMove() =>
        Assert.Equal(8, (int)AiProvider.Host);
}

public sealed class ModelsGeneratorFacts
{
    private sealed class InMemoryAdditionalText(string path, string content) : AdditionalText
    {
        public override string Path => path;
        public override SourceText? GetText(CancellationToken cancellationToken = default) => SourceText.From(content);
    }

    [Fact]
    public void RejectsUnknownMemberWithICAI001()
    {
        var compilation = CSharpCompilation.Create("Test", options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var additional = ImmutableArray.Create<AdditionalText>(
            new InMemoryAdditionalText("models.json", """{"version":1,"models":[{"id":"a","member":"A","oops":1}]}"""));

        var driver = CSharpGeneratorDriver
            .Create(new ModelsGenerator())
            .AddAdditionalTexts(additional);

        driver = driver.RunGenerators(compilation, TestContext.Current.CancellationToken);
        var result = driver.GetRunResult();
        var diagnostic = Assert.Single(result.Diagnostics, static d => d.Id == "ICAI001");
        Assert.Contains("oops", diagnostic.GetMessage(), StringComparison.Ordinal);
    }
}
