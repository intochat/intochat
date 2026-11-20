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
    public void FrozenIconVocabularyMatchesTheCanonical41Keys()
    {
        Assert.Equal(41, ExpectedFrozenIconKeys.Length);
        Assert.Equal(
            ExpectedFrozenIconKeys.OrderBy(static k => k, StringComparer.Ordinal),
            VisualCatalog.AllIconKeys.OrderBy(static k => k, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("IAspire", "Aspire", "brand:aspire", "#512BD4", "Developer")]
    [InlineData("aspire", "Aspire", "brand:aspire", "#512BD4", "Developer")]
    [InlineData("diagnostics.aspire", "Aspire", "brand:aspire", "#512BD4", "Developer")]
    [InlineData("IntoChat.OS.Modules.Microsoft.Aspire.IAspire", "Aspire", "brand:aspire", "#512BD4", "Developer")]
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
    [InlineData("IInoStudio", "Ino Studio", "brand:intochat", "#6366F1", "Studio")]
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
    [InlineData("claude-sonnet-4-6", null, "Sonnet 4.6", "provider:anthropic", "#D97757", "Anthropic")]
    [InlineData("sonnet-4.6", null, "Sonnet 4.6", "provider:anthropic", "#D97757", "Anthropic")]
    [InlineData("ISonnet46", null, "Sonnet 4.6", "provider:anthropic", "#D97757", "Anthropic")]
    [InlineData("claude-opus-4-6", null, "Opus 4.6", "provider:anthropic", "#D97757", "Anthropic")]
    [InlineData("IOpus46", null, "Opus 4.6", "provider:anthropic", "#D97757", "Anthropic")]
    [InlineData("claude-sonnet-5", null, "Sonnet 5", "provider:anthropic", "#D97757", "Anthropic")]
    [InlineData("claude-haiku-4-5", null, "Haiku 4.5", "provider:anthropic", "#D97757", "Anthropic")]
    [InlineData("gpt-5.4", null, "GPT-5.4", "provider:openai", "#10A37F", "OpenAI")]
    [InlineData("gpt-5", null, "GPT-5", "provider:openai", "#10A37F", "OpenAI")]
    [InlineData("gpt-4o", null, "GPT-4o", "provider:openai", "#10A37F", "OpenAI")]
    [InlineData("gemini-3.6-pro", null, "Gemini 3.6 Pro", "provider:google", "#4285F4", "Google")]
    [InlineData("gemini-3.6-flash", null, "Gemini 3.6 Flash", "provider:google", "#4285F4", "Google")]
    [InlineData("gemini-2.5-pro", null, "Gemini 2.5 Pro", "provider:google", "#4285F4", "Google")]
    [InlineData("qwen2.5-coder:14b", null, "Qwen 2.5 Coder 14B", "provider:ollama", "#6366F1", "Ollama (Local GPU)")]
    [InlineData("qwen3:14b", null, "Qwen 3 14B", "provider:ollama", "#6366F1", "Ollama (Local GPU)")]
    [InlineData("grok-4.6", "xai", "Grok 4.6", "provider:xai", "#18181B", "xAI")]
    [InlineData("openrouter/anthropic/claude-sonnet-4-6", "openrouter", "Sonnet 4.6", "provider:openrouter", "#6467F2", "OpenRouter")]
    [InlineData("phi-4-mini", "foundry", "Phi 4 Mini", "provider:foundry", "#0078D4", "Foundry Local")]
    public void ForModelResolvesFirstClassModelsAndProviders(
        string modelId,
        string? provider,
        string expectedLabel,
        string expectedIconKey,
        string expectedAccent,
        string expectedCategory)
    {
        var visual = VisualCatalog.ForModel(modelId, provider);
        Assert.Equal(new VisualDescriptor(expectedLabel, expectedIconKey, expectedAccent, expectedCategory), visual);
        Assert.Contains(visual.IconKey, (IReadOnlySet<string>)VisualCatalog.AllIconKeys);
    }
}
