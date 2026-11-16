using System.Collections.Frozen;
using System.Globalization;
using System.Text;

namespace IntoChat.OS.Framework.Visuals;

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

    private static readonly FrozenDictionary<string, VisualDescriptor> Contracts = CreateContracts();
    private static readonly FrozenDictionary<string, VisualDescriptor> Models = CreateModels();

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
            "foundry" or "foundrylocal" or "foundry-local" or "provider:foundry" => FoundryProvider,
            _ => new(provider.Trim(), "brand:intochat", "#6366F1", provider.Trim()),
        };
    }

    public static VisualDescriptor ForModel(string? modelId, string? provider = null)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return provider is { Length: > 0 }
                ? ForProvider(provider)
                : new("Automatic", "brand:intochat", "#6366F1", "Workspace Default");
        }

        var key = modelId.Trim();
        if (key.StartsWith("preset:", StringComparison.OrdinalIgnoreCase))
        {
            key = key["preset:".Length..].Trim();
        }
        else if (key.StartsWith("profile:", StringComparison.OrdinalIgnoreCase))
        {
            key = key["profile:".Length..].Trim();
        }

        string? explicitPrefixProvider = null;
        if (key.StartsWith("ollama/", StringComparison.OrdinalIgnoreCase) || key.StartsWith("ollama:", StringComparison.OrdinalIgnoreCase))
        {
            explicitPrefixProvider = "Ollama";
            key = key[7..].Trim();
        }
        else if (key.StartsWith("anthropic/", StringComparison.OrdinalIgnoreCase))
        {
            explicitPrefixProvider = "Anthropic";
            key = key["anthropic/".Length..].Trim();
        }
        else if (key.StartsWith("openai/", StringComparison.OrdinalIgnoreCase))
        {
            explicitPrefixProvider = "OpenAI";
            key = key["openai/".Length..].Trim();
        }
        else if (key.StartsWith("google/", StringComparison.OrdinalIgnoreCase))
        {
            explicitPrefixProvider = "Google";
            key = key["google/".Length..].Trim();
        }
        else if (key.StartsWith("xai/", StringComparison.OrdinalIgnoreCase))
        {
            explicitPrefixProvider = "XAI";
            key = key["xai/".Length..].Trim();
        }
        else if (key.StartsWith("openrouter/", StringComparison.OrdinalIgnoreCase))
        {
            explicitPrefixProvider = "OpenRouter";
            key = key["openrouter/".Length..].Trim();
        }
        else if (key.StartsWith("foundry/", StringComparison.OrdinalIgnoreCase))
        {
            explicitPrefixProvider = "FoundryLocal";
            key = key["foundry/".Length..].Trim();
        }

        var effectiveProvider = explicitPrefixProvider ?? provider;

        if (Models.TryGetValue(key, out var exact))
        {
            if (!string.IsNullOrWhiteSpace(effectiveProvider))
            {
                var prov = ForProvider(effectiveProvider);
                if (prov.IconKey.StartsWith("provider:", StringComparison.Ordinal) && prov.IconKey != exact.IconKey)
                {
                    return new(exact.Label, prov.IconKey, prov.AccentHex, prov.Category);
                }
            }

            return exact;
        }

        var inferredProvider = InferProviderFromModelId(key, effectiveProvider);
        var formattedLabel = FormatDynamicModelLabel(key, inferredProvider);
        return inferredProvider with { Label = formattedLabel };
    }

    private static VisualDescriptor InferProviderFromModelId(string modelId, string? explicitProvider)
    {
        if (!string.IsNullOrWhiteSpace(explicitProvider))
        {
            var resolved = ForProvider(explicitProvider);
            if (resolved.IconKey.StartsWith("provider:", StringComparison.Ordinal))
            {
                return resolved;
            }
        }

        if (modelId.StartsWith("claude", StringComparison.OrdinalIgnoreCase)
            || modelId.StartsWith("sonnet", StringComparison.OrdinalIgnoreCase)
            || modelId.StartsWith("opus", StringComparison.OrdinalIgnoreCase)
            || modelId.StartsWith("haiku", StringComparison.OrdinalIgnoreCase))
        {
            return AnthropicProvider;
        }

        if (modelId.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase)
            || modelId.StartsWith("o1", StringComparison.OrdinalIgnoreCase)
            || modelId.StartsWith("o3", StringComparison.OrdinalIgnoreCase)
            || modelId.StartsWith("o4", StringComparison.OrdinalIgnoreCase)
            || modelId.StartsWith("text-embedding-", StringComparison.OrdinalIgnoreCase)
            || modelId.StartsWith("whisper-", StringComparison.OrdinalIgnoreCase))
        {
            return OpenAIProvider;
        }

        if (modelId.StartsWith("gemini", StringComparison.OrdinalIgnoreCase))
        {
            return GoogleProvider;
        }

        if (modelId.StartsWith("grok", StringComparison.OrdinalIgnoreCase))
        {
            return XaiProvider;
        }

        if (modelId.StartsWith("foundry", StringComparison.OrdinalIgnoreCase))
        {
            return FoundryProvider;
        }

        if (modelId.Contains('/', StringComparison.Ordinal))
        {
            return OpenRouterProvider;
        }

        if (modelId.Contains(':', StringComparison.Ordinal)
            || modelId.StartsWith("qwen", StringComparison.OrdinalIgnoreCase)
            || modelId.StartsWith("gemma", StringComparison.OrdinalIgnoreCase)
            || modelId.StartsWith("llama", StringComparison.OrdinalIgnoreCase)
            || modelId.StartsWith("mistral", StringComparison.OrdinalIgnoreCase)
            || modelId.StartsWith("deepseek", StringComparison.OrdinalIgnoreCase)
            || modelId.StartsWith("phi", StringComparison.OrdinalIgnoreCase)
            || modelId.StartsWith("codellama", StringComparison.OrdinalIgnoreCase))
        {
            return OllamaProvider;
        }

        return new("AI Model", "brand:intochat", "#6366F1", "IntoChat OS");
    }

    private static string FormatDynamicModelLabel(string rawModelId, VisualDescriptor provider)
    {
        var clean = rawModelId;
        if (provider.IconKey == "provider:openrouter" && clean.Contains('/', StringComparison.Ordinal))
        {
            clean = clean[(clean.IndexOf('/', StringComparison.Ordinal) + 1)..];
        }

        if (clean.StartsWith("claude-", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean["claude-".Length..];
        }

        if (clean.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase))
        {
            var rest = clean["gpt-".Length..];
            var parts = rest.Split('-', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1)
            {
                return $"GPT-{parts[0]}";
            }

            return $"GPT-{parts[0]} {string.Join(' ', parts.Skip(1).Select(TitleWord))}";
        }

        if (clean.Contains(':', StringComparison.Ordinal))
        {
            var colon = clean.IndexOf(':', StringComparison.Ordinal);
            var family = FormatHyphenatedFamily(clean[..colon]);
            var size = clean[(colon + 1)..].ToUpperInvariant();
            return string.IsNullOrWhiteSpace(size) ? family : $"{family} {size}";
        }

        return FormatHyphenatedFamily(clean);
    }

    private static string FormatHyphenatedFamily(string value)
    {
        var tokens = value.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return value;
        }

        var sb = new StringBuilder();
        for (var i = 0; i < tokens.Length; i++)
        {
            if (i > 0)
            {
                // Merge trailing minor digit like "sonnet-4-6" -> "Sonnet 4.6"
                var prevDigits = tokens[i - 1].All(char.IsDigit);
                var currDigits = tokens[i].All(char.IsDigit) && tokens[i].Length <= 2;
                sb.Append(prevDigits && currDigits ? '.' : ' ');
            }

            sb.Append(FormatFamilyToken(tokens[i]));
        }

        return sb.ToString();
    }

    private static string FormatFamilyToken(string token)
    {
        // Split leading letters from trailing version digits: e.g. "qwen2.5" -> "Qwen 2.5", "llama3.3" -> "Llama 3.3"
        var splitAt = -1;
        for (var i = 1; i < token.Length; i++)
        {
            if (char.IsLetter(token[i - 1]) && char.IsDigit(token[i]))
            {
                splitAt = i;
                break;
            }
        }

        if (splitAt > 0 && !token.Equals("4o", StringComparison.OrdinalIgnoreCase) && !token.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            return $"{TitleWord(token[..splitAt])} {token[splitAt..]}";
        }

        return TitleWord(token);
    }

    private static string TitleWord(string word)
    {
        if (string.IsNullOrEmpty(word))
        {
            return word;
        }

        if (word.All(char.IsDigit))
        {
            return word;
        }

        if (word.Length <= 3 && word.StartsWith('v') && word[1..].All(c => char.IsDigit(c) || c == '.'))
        {
            return "V" + word[1..];
        }

        return char.ToUpperInvariant(word[0]) + word[1..];
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
            "IGitHub", "IWorkTree", "IGitHubRepository", "IFeatureToPr", "github", "worktree", "f2pr", "brand:github");

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

    private static FrozenDictionary<string, VisualDescriptor> CreateModels()
    {
        var entries = new List<KeyValuePair<string, VisualDescriptor>>(96);
        void Add(VisualDescriptor descriptor, params ReadOnlySpan<string> keys)
        {
            entries.Add(new(descriptor.Label, descriptor));
            foreach (var key in keys)
            {
                entries.Add(new(key, descriptor));
            }
        }

        // Anthropic
        Add(new("Sonnet 4.6", "provider:anthropic", "#D97757", "Anthropic"),
            "claude-sonnet-4-6", "sonnet-4.6", "sonnet-4-6", "sonnet46", "ISonnet46");
        Add(new("Opus 4.6", "provider:anthropic", "#D97757", "Anthropic"),
            "claude-opus-4-6", "opus-4.6", "opus-4-6", "opus46", "IOpus46");
        Add(new("Sonnet 4.5", "provider:anthropic", "#D97757", "Anthropic"),
            "claude-sonnet-4-5", "sonnet-4.5", "sonnet-4-5", "sonnet45");
        Add(new("Sonnet 5", "provider:anthropic", "#D97757", "Anthropic"),
            "claude-sonnet-5", "sonnet-5", "sonnet5", "ISonnet5");
        Add(new("Opus 5", "provider:anthropic", "#D97757", "Anthropic"),
            "claude-opus-5", "opus-5", "opus5", "IOpus5");
        Add(new("Fable 5", "provider:anthropic", "#D97757", "Anthropic"),
            "claude-fable-5", "fable-5", "fable5", "IFable5");
        Add(new("Haiku 4.5", "provider:anthropic", "#D97757", "Anthropic"),
            "claude-haiku-4-5", "haiku-4.5", "haiku-4-5", "haiku45", "IHaiku45");

        // OpenAI
        Add(new("GPT-5.6 Sol", "provider:openai", "#10A37F", "OpenAI"),
            "gpt-5.6-sol", "gpt56sol", "IGpt56Sol");
        Add(new("GPT-5.6 Terra", "provider:openai", "#10A37F", "OpenAI"),
            "gpt-5.6-terra", "gpt56terra", "IGpt56Terra");
        Add(new("GPT-5.6 Luna", "provider:openai", "#10A37F", "OpenAI"),
            "gpt-5.6-luna", "gpt56luna", "IGpt56Luna");
        Add(new("GPT-5.4", "provider:openai", "#10A37F", "OpenAI"),
            "gpt-5.4", "gpt54", "IGpt54");
        Add(new("GPT-5.4 Mini", "provider:openai", "#10A37F", "OpenAI"),
            "gpt-5.4-mini", "gpt54mini", "IGpt54Mini");
        Add(new("GPT-5.4 Nano", "provider:openai", "#10A37F", "OpenAI"),
            "gpt-5.4-nano", "gpt54nano", "IGpt54Nano");
        Add(new("GPT-5", "provider:openai", "#10A37F", "OpenAI"),
            "gpt-5");
        Add(new("GPT-4o", "provider:openai", "#10A37F", "OpenAI"),
            "gpt-4o");
        Add(new("GPT-4o Mini", "provider:openai", "#10A37F", "OpenAI"),
            "gpt-4o-mini");

        // Google
        Add(new("Gemini 3.6 Pro", "provider:google", "#4285F4", "Google"),
            "gemini-3.6-pro", "gemini36pro", "IGemini36Pro");
        Add(new("Gemini 3.6 Flash", "provider:google", "#4285F4", "Google"),
            "gemini-3.6-flash", "gemini36flash", "IGemini36Flash");
        Add(new("Gemini 3.1 Pro", "provider:google", "#4285F4", "Google"),
            "gemini-3.1-pro", "gemini-3.1-pro-preview", "gemini31pro", "IGemini31Pro");
        Add(new("Gemini 2.5 Pro", "provider:google", "#4285F4", "Google"),
            "gemini-2.5-pro");
        Add(new("Gemini 2.5 Flash", "provider:google", "#4285F4", "Google"),
            "gemini-2.5-flash");

        // Ollama (Local GPU)
        Add(new("Qwen 2.5 Coder 14B", "provider:ollama", "#6366F1", "Ollama (Local GPU)"),
            "qwen2.5-coder:14b", "qwen2.5-coder");
        Add(new("Qwen 3 14B", "provider:ollama", "#6366F1", "Ollama (Local GPU)"),
            "qwen3:14b");
        Add(new("Qwen 3.5 4B", "provider:ollama", "#6366F1", "Ollama (Local GPU)"),
            IntoChat.OS.Contracts.Configuration.IntoChatOSNames.LocalDefaultChatModel, "qwen35", "IQwen35");
        Add(new("Gemma 4 E2B", "provider:ollama", "#6366F1", "Ollama (Local GPU)"),
            "gemma4:e2b", "gemma4", "IGemma4");

        // xAI
        Add(new("Grok 4.6", "provider:xai", "#18181B", "xAI"),
            "grok-4.6", "grok46", "IGrok46");

        // OpenRouter
        Add(new("DeepSeek V4.1 Flash", "provider:openrouter", "#6467F2", "OpenRouter"),
            "deepseek/deepseek-v4.1-flash", "deepseekv41flash", "IDeepSeekV41Flash");

        // Foundry Local
        Add(new("Whisper Large V3 Turbo", "provider:foundry", "#0078D4", "Foundry Local"),
            "whisper-large-v3-turbo", "IWhisperLargeV3Turbo");
        Add(new("Whisper Small", "provider:foundry", "#0078D4", "Foundry Local"),
            "whisper-small", "IWhisperSmall");
        Add(new("Whisper Tiny", "provider:foundry", "#0078D4", "Foundry Local"),
            "whisper-tiny", "IWhisperTiny");

        // Providers directly passed as model keys
        entries.Add(new("xai", XaiProvider));
        entries.Add(new("openrouter", OpenRouterProvider));
        entries.Add(new("foundry", FoundryProvider));
        entries.Add(new("foundry-local", FoundryProvider));
        entries.Add(new("qwen", QwenProvider));

        return entries.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }
}
