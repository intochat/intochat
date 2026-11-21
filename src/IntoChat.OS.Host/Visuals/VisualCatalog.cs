using System.Collections.Frozen;
using System.Globalization;
using System.Text;

namespace IntoChat.OS.Host.Visuals;

/// <summary>
/// Immutable visual identity metadata shared across C# views, model catalogs, and shell projections.
/// IconKey always belongs to the frozen allowlisted vocabulary (<c>brand:*</c>, <c>provider:*</c>,
/// <c>capability:*</c>, <c>artcraft:*</c>) so UI surfaces resolve bundled local assets without external URLs.
/// </summary>
public readonly record struct VisualDescriptor(
    string Label,
    string IconKey,
    string AccentHex,
    string Category);

/// <summary>
/// Allocation-light, pure static resolver for brand, capability, studio, AI provider, and model visuals.
/// </summary>
public static class VisualCatalog
{
    /// <summary>Resolves the canonical capability descriptor (<c>IconKey</c>, <c>Label</c>, <c>AccentHex</c>) for an approval card subject.</summary>
    public static VisualDescriptor ForApproval(string? sourceOrSubject)
        => TryForContract(sourceOrSubject, out var found) ? found
            : !string.IsNullOrWhiteSpace(sourceOrSubject) && sourceOrSubject.Trim().StartsWith("change-", StringComparison.OrdinalIgnoreCase)
                ? ForContract("IGitHub")
                : ForContract("IConfirmations");

    public static readonly FrozenSet<string> AllIconKeys = new[]
    {
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
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly VisualDescriptor DefaultContractVisual =
        new("Capability", "capability:studio", "#64748B", "System");

    private static readonly VisualDescriptor AnthropicProvider =
        new("Anthropic", "provider:anthropic", "#D97757", "Anthropic");

    private static readonly VisualDescriptor OpenAIProvider =
        new("OpenAI", "provider:openai", "#10A37F", "OpenAI");

    private static readonly VisualDescriptor GoogleProvider =
        new("Google", "provider:google", "#4285F4", "Google");

    private static readonly VisualDescriptor OllamaProvider =
        new("Ollama (Local GPU)", "provider:ollama", "#6366F1", "Ollama (Local GPU)");

    private static readonly VisualDescriptor QwenProvider =
        new("Qwen", "provider:qwen", "#6366F1", "Qwen");

    private static readonly VisualDescriptor XaiProvider =
        new("xAI", "provider:xai", "#18181B", "xAI");

    private static readonly VisualDescriptor OpenRouterProvider =
        new("OpenRouter", "provider:openrouter", "#6467F2", "OpenRouter");

    private static readonly VisualDescriptor FoundryProvider =
        new("Foundry Local", "provider:foundry", "#0078D4", "Foundry Local");

    private static readonly VisualDescriptor OpenAICompatibleProvider =
        new("OpenAI-Compatible", "provider:openaicompatible", "#10A37F", "OpenAI-Compatible");

    private static readonly FrozenDictionary<string, VisualDescriptor> Contracts = CreateContracts();

    public static VisualDescriptor ForContract(string? contractOrKey)
    {
        if (TryForContract(contractOrKey, out var descriptor))
        {
            return descriptor;
        }

        if (string.IsNullOrWhiteSpace(contractOrKey))
        {
            return DefaultContractVisual;
        }

        var fallbackLabel = HumanizeContractToken(contractOrKey);
        return string.IsNullOrWhiteSpace(fallbackLabel)
            ? DefaultContractVisual
            : DefaultContractVisual with { Label = fallbackLabel };
    }

    public static bool TryForContract(string? contractOrKey, out VisualDescriptor descriptor)
    {
        if (string.IsNullOrWhiteSpace(contractOrKey))
        {
            descriptor = default;
            return false;
        }

        var trimmed = StripUriAndHash(contractOrKey);
        if (Contracts.TryGetValue(trimmed, out descriptor))
        {
            return true;
        }

        // Normalize slash-delimited keys such as "owner/diagnostics/aspire", "owner/apps/photocraft", "local/roslyn/codegraph".
        if (trimmed.Contains('/', StringComparison.Ordinal))
        {
            var slashNormalized = trimmed.Replace('/', '.');
            if (Contracts.TryGetValue(slashNormalized, out descriptor))
            {
                return true;
            }

            var firstSlash = trimmed.IndexOf('/', StringComparison.Ordinal);
            if (firstSlash >= 0 && firstSlash + 1 < trimmed.Length)
            {
                var withoutScope = trimmed[(firstSlash + 1)..];
                if (Contracts.TryGetValue(withoutScope, out descriptor)
                    || Contracts.TryGetValue(withoutScope.Replace('/', '.'), out descriptor))
                {
                    return true;
                }

                if (withoutScope.StartsWith("apps/", StringComparison.OrdinalIgnoreCase)
                    && Contracts.TryGetValue(withoutScope["apps/".Length..], out descriptor))
                {
                    return true;
                }

                if (withoutScope.StartsWith("artcraft/", StringComparison.OrdinalIgnoreCase)
                    && Contracts.TryGetValue(withoutScope["artcraft/".Length..], out descriptor))
                {
                    return true;
                }
            }

            var lastSegment = trimmed[(trimmed.LastIndexOf('/') + 1)..];
            if (Contracts.TryGetValue(lastSegment, out descriptor))
            {
                return true;
            }
        }

        // Normalize namespace-qualified CLR contract or contract+method strings:
        // e.g. "IntoChat.OS.Modules.Microsoft.Aspire.IAspire" or "...IAspire.GetResourcesAsync".
        if (trimmed.Contains('.', StringComparison.Ordinal))
        {
            var segments = trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries);
            for (var i = segments.Length - 1; i >= 0; i--)
            {
                var segment = segments[i];
                if (Contracts.TryGetValue(segment, out descriptor))
                {
                    return true;
                }

                if (segment.Contains('/', StringComparison.Ordinal))
                {
                    var leaf = segment[(segment.LastIndexOf('/') + 1)..];
                    if (Contracts.TryGetValue(leaf, out descriptor))
                    {
                        return true;
                    }
                }
            }

            if (segments.Length >= 2)
            {
                var tailTwo = $"{segments[^2]}.{segments[^1]}";
                if (Contracts.TryGetValue(tailTwo, out descriptor))
                {
                    return true;
                }
            }
        }

        descriptor = default;
        return false;
    }

    public static VisualDescriptor ForProvider(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return new("AI Model", "brand:intochat", "#6366F1", "IntoChat OS");
        }

        var normalized = provider.Trim().ToLowerInvariant();
        return normalized switch
        {
            "anthropic" or "claude" or "provider:anthropic" => AnthropicProvider,
            "openai" or "provider:openai" => OpenAIProvider,
            "google" or "gemini" or "provider:google" => GoogleProvider,
            "ollama" or "local" or "provider:ollama" => OllamaProvider,
            "qwen" or "provider:qwen" => QwenProvider,
            "xai" or "grok" or "provider:xai" => XaiProvider,
            "openrouter" or "provider:openrouter" => OpenRouterProvider,
            "foundry" or "foundrylocal" or "foundry-local" or "provider:foundry" or "provider:foundrylocal" => FoundryProvider,
            "openaicompatible" or "openai-compatible" or "openai-compat" or "provider:openaicompatible" => OpenAICompatibleProvider,
            _ => new(provider.Trim(), "brand:intochat", "#6366F1", provider.Trim()),
        };
    }

    private static string StripUriAndHash(string raw)
    {
        var text = raw.Trim();
        if (text.StartsWith("neuron://", StringComparison.OrdinalIgnoreCase))
        {
            text = text["neuron://".Length..];
        }

        var hash = text.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0)
        {
            text = text[..hash];
        }

        return text.Trim();
    }

    private static string HumanizeContractToken(string raw)
    {
        var clean = StripUriAndHash(raw);
        if (clean.Contains('/', StringComparison.Ordinal))
        {
            clean = clean[(clean.LastIndexOf('/') + 1)..];
        }

        if (clean.Contains('.', StringComparison.Ordinal))
        {
            clean = clean[(clean.LastIndexOf('.') + 1)..];
        }

        if (clean.Length > 1 && clean[0] == 'I' && char.IsUpper(clean[1]))
        {
            clean = clean[1..];
        }

        var sb = new StringBuilder(clean.Length + 4);
        for (var i = 0; i < clean.Length; i++)
        {
            var c = clean[i];
            if (c is '-' or '_')
            {
                sb.Append(' ');
                continue;
            }

            if (i > 0 && char.IsUpper(c) && (char.IsLower(clean[i - 1]) || char.IsDigit(clean[i - 1])))
            {
                sb.Append(' ');
            }

            sb.Append(i == 0 ? char.ToUpper(c, CultureInfo.InvariantCulture) : c);
        }

        return sb.ToString().Trim();
    }

    private static FrozenDictionary<string, VisualDescriptor> CreateContracts()
    {
        var entries = new List<KeyValuePair<string, VisualDescriptor>>(128);
        void Add(VisualDescriptor descriptor, params ReadOnlySpan<string> keys)
        {
            entries.Add(new(descriptor.Label, descriptor));
            foreach (var key in keys)
            {
                entries.Add(new(key, descriptor));
            }
        }

        // Core Developer, Connector, Intelligence, Studio, Diagnostics, Network, System, Security capabilities
        Add(new("Aspire", "brand:aspire", "#512BD4", "Developer"),
            "IAspire", "aspire", "diagnostics.aspire", "diagnostics/aspire", "brand:aspire");

        Add(new("GitHub", "brand:github", "#24292F", "Developer"),
            "IGitHub", "IWorkTree", "IGitHubRepository", "github", "worktree", "f2pr", "brand:github");

        Add(new("Gmail", "brand:gmail", "#EA4335", "Connector"),
            "IGmail", "gmail", "google.gmail", "brand:gmail");

        Add(new("Salesforce", "brand:salesforce", "#00A1E0", "Connector"),
            "ISalesforce", "salesforce", "brand:salesforce");

        Add(new("Memory", "capability:memory", "#8B5CF6", "Intelligence"),
            "IMemory", "IKnowledgeStore", "ai.memory", "memory", "capability:memory");

        Add(new("CodeGraph", "capability:codegraph", "#10B981", "Developer"),
            "ICodeGraph", "IRoslyn", "roslyn.codegraph", "roslyn/codegraph", "microsoft.roslyn", "codegraph", "roslyn", "capability:codegraph");

        Add(new("Ino Studio", "brand:intochat", "#6366F1", "Studio"),
            "IInoStudio", "ISynapseStudio", "ino.studio", "ino/studio", "synapse.studio", "ino", "brand:intochat");

        Add(new("System Traces", "capability:traces", "#F59E0B", "Diagnostics"),
            "ITraceReader", "trace.reader", "traces", "capability:traces");

        Add(new("Web HTTPS", "capability:http", "#0EA5E9", "Network"),
            "IHttpRead", "http@1", "http", "capability:http");

        Add(new("Notifications", "capability:notifications", "#EC4899", "System"),
            "INotifications", "notifications", "os.notifications", "capability:notifications");

        Add(new("Human Approval", "capability:shield", "#F97316", "Security"),
            "IConfirmations", "IApprovals", "confirmations", "os.confirmations", "approvals", "capability:shield");

        Add(new("Files", "capability:files", "#3B82F6", "System"),
            "IInoFiles", "IFilesWorkspace", "IFilesNeuron", "IFilesVfsService", "files", "files.vfs", "capability:files");

        Add(new("App Studio", "capability:studio", "#6366F1", "Studio"),
            "IAppStudio", "creator", "capability:studio");

        Add(new("Docker", "brand:docker", "#2496ED", "Developer"),
            "docker", "brand:docker");

        Add(new(".NET", "brand:dotnet", "#512BD4", "Developer"),
            "dotnet", "brand:dotnet");

        Add(new("Bitcoin", "brand:bitcoin", "#F7931A", "Finance"),
            "bitcoin", "btcalert", "btc", "brand:bitcoin");

        // AI Providers
        Add(AnthropicProvider, "provider:anthropic");
        Add(OpenAIProvider, "provider:openai");
        Add(GoogleProvider, "provider:google");
        Add(OllamaProvider, "provider:ollama");
        Add(QwenProvider, "provider:qwen");
        Add(XaiProvider, "provider:xai");
        Add(OpenRouterProvider, "provider:openrouter");
        Add(FoundryProvider, "provider:foundry");
        entries.Add(new("provider:foundrylocal", FoundryProvider with { IconKey = "provider:foundrylocal" }));
        Add(OpenAICompatibleProvider, "provider:openaicompatible");

        // All 17 ArtCraft Studios + IArtCraftStudio contract
        Add(new("ArtCraft", "artcraft:artcraft", "#6366F1", "ArtCraft Studio"),
            "IArtCraftStudio", "IArtCraftEngine", "artcraft", "craft", "craft-launcher", "artcraft:artcraft");
        Add(new("PhotoCraft", "artcraft:photocraft", "#06B6D4", "ArtCraft Studio"),
            "photocraft", "artcraft:photocraft");
        Add(new("FilmCraft", "artcraft:filmcraft", "#8B5CF6", "ArtCraft Studio"),
            "filmcraft", "videocraft", "artcraft:filmcraft");
        Add(new("VectorCraft", "artcraft:vectorcraft", "#F59E0B", "ArtCraft Studio"),
            "vectorcraft", "artcraft:vectorcraft");
        Add(new("LightCraft", "artcraft:lightcraft", "#38BDF8", "ArtCraft Studio"),
            "lightcraft", "artcraft:lightcraft");
        Add(new("EffectCraft", "artcraft:effectcraft", "#A855F7", "ArtCraft Studio"),
            "effectcraft", "artcraft:effectcraft");
        Add(new("DesignCraft", "artcraft:designcraft", "#EC4899", "ArtCraft Studio"),
            "designcraft", "artcraft:designcraft");
        Add(new("PdfCraft", "artcraft:pdfcraft", "#EF4444", "ArtCraft Studio"),
            "pdfcraft", "artcraft:pdfcraft");
        Add(new("WordCraft", "artcraft:wordcraft", "#2563EB", "ArtCraft Studio"),
            "wordcraft", "artcraft:wordcraft");
        Add(new("SoundCraft", "artcraft:soundcraft", "#10B981", "ArtCraft Studio"),
            "soundcraft", "artcraft:soundcraft");
        Add(new("DeckCraft", "artcraft:deckcraft", "#F97316", "ArtCraft Studio"),
            "deckcraft", "artcraft:deckcraft");
        Add(new("CADCraft", "artcraft:cadcraft", "#0EA5E9", "ArtCraft Studio"),
            "cadcraft", "artcraft:cadcraft");
        Add(new("GridCraft", "artcraft:gridcraft", "#16A34A", "ArtCraft Studio"),
            "gridcraft", "artcraft:gridcraft");
        Add(new("FigCraft", "artcraft:figcraft", "#F43F5E", "ArtCraft Studio"),
            "figcraft", "artcraft:figcraft");
        Add(new("TextureCraft", "artcraft:texturecraft", "#D97706", "ArtCraft Studio"),
            "texturecraft", "artcraft:texturecraft");
        Add(new("MaterialCraft", "artcraft:materialcraft", "#EA580C", "ArtCraft Studio"),
            "materialcraft", "artcraft:materialcraft");
        Add(new("SolidCraft", "artcraft:solidcraft", "#14B8A6", "ArtCraft Studio"),
            "solidcraft", "artcraft:solidcraft");

        return entries.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }
}
