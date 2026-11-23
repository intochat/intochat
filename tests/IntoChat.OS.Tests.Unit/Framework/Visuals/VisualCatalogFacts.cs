using IntoChat.OS.Host.Visuals;

namespace IntoChat.OS.Tests.Unit.Framework.Visuals;

public sealed class VisualCatalogFacts
{
    private static readonly string[] ExpectedFrozenIconKeys =
    [
        // Brands
        "brand:aspire",
        "brand:github",
        "brand:gmail",
        "brand:salesforce",
        "brand:intochat",
        "brand:docker",
        "brand:dotnet",
        "brand:bitcoin",
        // Providers
        "provider:anthropic",
        "provider:openai",
        "provider:google",
        "provider:ollama",
        "provider:qwen",
        "provider:xai",
        "provider:openrouter",
        "provider:foundry",
        "provider:foundrylocal",
        "provider:openaicompatible",
        "provider:sherpaonnx",
        // Capabilities
        "capability:memory",
        "capability:codegraph",
        "capability:traces",
        "capability:http",
        "capability:notifications",
        "capability:shield",
        "capability:files",
        "capability:studio",
        // ArtCraft Studios (17)
        "artcraft:artcraft",
        "artcraft:photocraft",
        "artcraft:filmcraft",
        "artcraft:vectorcraft",
        "artcraft:lightcraft",
        "artcraft:effectcraft",
        "artcraft:designcraft",
        "artcraft:pdfcraft",
        "artcraft:wordcraft",
        "artcraft:soundcraft",
        "artcraft:deckcraft",
        "artcraft:cadcraft",
        "artcraft:gridcraft",
        "artcraft:figcraft",
        "artcraft:texturecraft",
        "artcraft:materialcraft",
        "artcraft:solidcraft",
    ];

    [Fact]
    public void FrozenIconVocabularyMatchesTheCanonicalKeys()
    {
        Assert.Equal(44, ExpectedFrozenIconKeys.Length);
        Assert.Equal(
            ExpectedFrozenIconKeys.OrderBy(static k => k, StringComparer.Ordinal),
            VisualCatalog.AllIconKeys.OrderBy(static k => k, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("IAspire", "Aspire", "brand:aspire", "#512BD4", "Developer")]
    [InlineData("aspire", "Aspire", "brand:aspire", "#512BD4", "Developer")]
    [InlineData("diagnostics.aspire", "Aspire", "brand:aspire", "#512BD4", "Developer")]
    [InlineData("IntoChat.OS.Modules.Microsoft.Aspire.IAspire", "Aspire", "brand:aspire", "#512BD4", "Developer")]
    [InlineData("IntoChat.Aspire.IAspire", "Aspire", "brand:aspire", "#512BD4", "Developer")]
    [InlineData("IGitHub", "GitHub", "brand:github", "#24292F", "Developer")]
    [InlineData("IWorkTree", "GitHub", "brand:github", "#24292F", "Developer")]
    [InlineData("github", "GitHub", "brand:github", "#24292F", "Developer")]
    [InlineData("IGmail", "Gmail", "brand:gmail", "#EA4335", "Connector")]
    [InlineData("gmail", "Gmail", "brand:gmail", "#EA4335", "Connector")]
    [InlineData("ISalesforce", "Salesforce", "brand:salesforce", "#00A1E0", "Connector")]
    [InlineData("salesforce", "Salesforce", "brand:salesforce", "#00A1E0", "Connector")]
    [InlineData("IMemory", "Memory", "capability:memory", "#8B5CF6", "Intelligence")]
    [InlineData("IKnowledgeStore", "Memory", "capability:memory", "#8B5CF6", "Intelligence")]
    [InlineData("ai.memory", "Memory", "capability:memory", "#8B5CF6", "Intelligence")]
    [InlineData("ICodeGraph", "CodeGraph", "capability:codegraph", "#10B981", "Developer")]
    [InlineData("roslyn.codegraph", "CodeGraph", "capability:codegraph", "#10B981", "Developer")]
    [InlineData("codegraph", "CodeGraph", "capability:codegraph", "#10B981", "Developer")]
    [InlineData("IStudio", "Ino Studio", "brand:intochat", "#6366F1", "Studio")]
    [InlineData("ino.studio", "Ino Studio", "brand:intochat", "#6366F1", "Studio")]
    [InlineData("ITraceReader", "System Traces", "capability:traces", "#F59E0B", "Diagnostics")]
    [InlineData("traces", "System Traces", "capability:traces", "#F59E0B", "Diagnostics")]
    [InlineData("IHttpRead", "Web HTTPS", "capability:http", "#0EA5E9", "Network")]
    [InlineData("http@1", "Web HTTPS", "capability:http", "#0EA5E9", "Network")]
    [InlineData("http", "Web HTTPS", "capability:http", "#0EA5E9", "Network")]
    [InlineData("INotifications", "Notifications", "capability:notifications", "#EC4899", "System")]
    [InlineData("notifications", "Notifications", "capability:notifications", "#EC4899", "System")]
    [InlineData("IConfirmations", "Human Approval", "capability:shield", "#F97316", "Security")]
    [InlineData("confirmations", "Human Approval", "capability:shield", "#F97316", "Security")]
    [InlineData("IArtCraftStudio", "ArtCraft", "artcraft:artcraft", "#6366F1", "ArtCraft Studio")]
    public void ForContractResolvesCoreCapabilitiesAndBrands(
        string contractOrKey,
        string expectedLabel,
        string expectedIconKey,
        string expectedAccent,
        string expectedCategory)
    {
        var visual = VisualCatalog.ForContract(contractOrKey);
        Assert.Equal(new VisualDescriptor(expectedLabel, expectedIconKey, expectedAccent, expectedCategory), visual);
        Assert.Contains(visual.IconKey, (IReadOnlySet<string>)VisualCatalog.AllIconKeys);
    }

    [Theory]
    [InlineData("artcraft", "ArtCraft", "artcraft:artcraft", "#6366F1")]
    [InlineData("photocraft", "PhotoCraft", "artcraft:photocraft", "#06B6D4")]
    [InlineData("filmcraft", "FilmCraft", "artcraft:filmcraft", "#8B5CF6")]
    [InlineData("vectorcraft", "VectorCraft", "artcraft:vectorcraft", "#F59E0B")]
    [InlineData("lightcraft", "LightCraft", "artcraft:lightcraft", "#38BDF8")]
    [InlineData("effectcraft", "EffectCraft", "artcraft:effectcraft", "#A855F7")]
    [InlineData("designcraft", "DesignCraft", "artcraft:designcraft", "#EC4899")]
    [InlineData("pdfcraft", "PdfCraft", "artcraft:pdfcraft", "#EF4444")]
    [InlineData("wordcraft", "WordCraft", "artcraft:wordcraft", "#2563EB")]
    [InlineData("soundcraft", "SoundCraft", "artcraft:soundcraft", "#10B981")]
    [InlineData("deckcraft", "DeckCraft", "artcraft:deckcraft", "#F97316")]
    [InlineData("cadcraft", "CADCraft", "artcraft:cadcraft", "#0EA5E9")]
    [InlineData("gridcraft", "GridCraft", "artcraft:gridcraft", "#16A34A")]
    [InlineData("figcraft", "FigCraft", "artcraft:figcraft", "#F43F5E")]
    [InlineData("texturecraft", "TextureCraft", "artcraft:texturecraft", "#D97706")]
    [InlineData("materialcraft", "MaterialCraft", "artcraft:materialcraft", "#EA580C")]
    [InlineData("solidcraft", "SolidCraft", "artcraft:solidcraft", "#14B8A6")]
    public void ForContractResolvesAll17ArtCraftStudios(
        string studioId,
        string expectedLabel,
        string expectedIconKey,
        string expectedAccent)
    {
        var byId = VisualCatalog.ForContract(studioId);
        Assert.Equal(new VisualDescriptor(expectedLabel, expectedIconKey, expectedAccent, "ArtCraft Studio"), byId);

        var byNeuronPath = VisualCatalog.ForContract($"owner/artcraft/{studioId}.ExecuteCommand");
        Assert.Equal(expectedLabel, byNeuronPath.Label);
        Assert.Equal(expectedIconKey, byNeuronPath.IconKey);
        Assert.Contains(byId.IconKey, (IReadOnlySet<string>)VisualCatalog.AllIconKeys);
    }

    [Theory]
    [InlineData("anthropic", "Anthropic", "provider:anthropic", "#D97757", "Anthropic")]
    [InlineData("openai", "OpenAI", "provider:openai", "#10A37F", "OpenAI")]
    [InlineData("google", "Google", "provider:google", "#4285F4", "Google")]
    [InlineData("ollama", "Ollama (Local GPU)", "provider:ollama", "#6366F1", "Ollama (Local GPU)")]
    [InlineData("xai", "xAI", "provider:xai", "#18181B", "xAI")]
    [InlineData("openrouter", "OpenRouter", "provider:openrouter", "#6467F2", "OpenRouter")]
    [InlineData("foundry", "Foundry Local", "provider:foundry", "#0078D4", "Foundry Local")]
    [InlineData("provider:foundrylocal", "Foundry Local", "provider:foundry", "#0078D4", "Foundry Local")]
    [InlineData("openaicompatible", "OpenAI-Compatible", "provider:openaicompatible", "#10A37F", "OpenAI-Compatible")]
    [InlineData("provider:openaicompatible", "OpenAI-Compatible", "provider:openaicompatible", "#10A37F", "OpenAI-Compatible")]
    [InlineData("sherpaonnx", "sherpa-onnx", "provider:sherpaonnx", "#10A37F", "sherpa-onnx")]
    [InlineData("provider:sherpaonnx", "sherpa-onnx", "provider:sherpaonnx", "#10A37F", "sherpa-onnx")]
    public void ForProviderResolvesCanonicalAndAliasProviders(
        string provider,
        string expectedLabel,
        string expectedIconKey,
        string expectedAccent,
        string expectedCategory)
    {
        var visual = VisualCatalog.ForProvider(provider);
        Assert.Equal(new VisualDescriptor(expectedLabel, expectedIconKey, expectedAccent, expectedCategory), visual);
        Assert.Contains(visual.IconKey, (IReadOnlySet<string>)VisualCatalog.AllIconKeys);
    }

    [Fact]
    public void VisualCatalogHasNoModelLookup()
        => Assert.Null(typeof(VisualCatalog).GetMethod("ForModel"));
}
