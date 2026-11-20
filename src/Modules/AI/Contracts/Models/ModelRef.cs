using System.Diagnostics.CodeAnalysis;

namespace IntoChat.OS.Modules.AI.Models;

/// <summary>Purpose of a model selection request: conversational assistant chat or coding/authoring.</summary>
public enum ModelUse
{
    Chat,
    Code,
}

/// <summary>Validated reference to a model: a catalogued model, a configured profile, or an explicit provider tag.</summary>
public abstract record ModelRef
{
    private ModelRef() { }

    public sealed record Catalogued(ModelCard Model) : ModelRef;
    public sealed record Profile(string Name) : ModelRef;
    public sealed record Tag(AiProvider Provider, string Id) : ModelRef;

    /// <summary>Strict parser accepting only configured profiles, catalogued models, or explicit provider prefixes.</summary>
    public static ModelRef Parse(string? raw, IReadOnlySet<string>? profileNames = null)
        => TryParse(raw, profileNames, out var parsed)
            ? parsed
            : throw new ArgumentException($"Unknown model '{raw?.Trim()}'. Use a catalogued model, profile name, or explicit provider tag.", nameof(raw));

    /// <summary>Attempts strict parsing without guessing bare uncatalogued model names.</summary>
    public static bool TryParse(
        string? raw,
        IReadOnlySet<string>? profileNames,
        [NotNullWhen(true)] out ModelRef? parsed)
    {
        parsed = null;
        if (!TryValidateRaw(raw, out var trimmed))
        {
            return false;
        }

        if (trimmed.StartsWith("profile:", StringComparison.OrdinalIgnoreCase))
        {
            var profileKey = trimmed["profile:".Length..].Trim();
            if (TryMatchProfile(profileKey, profileNames, out var matched))
            {
                parsed = new Profile(matched);
                return true;
            }
            return false;
        }

        if (TryMatchProfile(trimmed, profileNames, out var matchedProfile))
        {
            parsed = new Profile(matchedProfile);
            return true;
        }

        if (KnownModels.Find(trimmed) is { } catalogued && catalogued.Can.HasFlag(Abilities.Text))
        {
            parsed = new Catalogued(catalogued);
            return true;
        }

        var unprefixed = StripPresetPrefix(trimmed);
        if (TryParseExplicitProviderTag(unprefixed, out var provider, out var modelId, out _))
        {
            parsed = new Tag(provider, modelId);
            return true;
        }

        return false;
    }

    /// <summary>Backward-compatible reader for persisted settings, context entries, and wire selections.</summary>
    public static ModelRef ReadStored(
        string? raw,
        IReadOnlySet<string>? profileNames = null,
        IEnumerable<string>? detectedLocalTags = null)
        => TryReadStored(raw, profileNames, detectedLocalTags, out var parsed)
            ? parsed
            : throw new ArgumentException($"Unknown model '{raw?.Trim()}'. Use a model marker name or a configured profile name.", nameof(raw));

    /// <summary>Attempts backward-compatible parsing of stored or legacy wire model strings.</summary>
    public static bool TryReadStored(
        string? raw,
        IReadOnlySet<string>? profileNames,
        IEnumerable<string>? detectedLocalTags,
        [NotNullWhen(true)] out ModelRef? parsed)
    {
        if (TryParse(raw, profileNames, out parsed))
        {
            return true;
        }

        parsed = null;
        if (!TryValidateRaw(raw, out var trimmed))
        {
            return false;
        }

        var candidate = StripPresetPrefix(trimmed);
        if (detectedLocalTags is not null)
        {
            foreach (var localTag in detectedLocalTags)
            {
                if (string.Equals(localTag, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    parsed = new Tag(AiProvider.Ollama, localTag);
                    return true;
                }
            }
        }

        if (TryInferLegacy(candidate, out var provider, out var modelId, out _))
        {
            parsed = new Tag(provider, modelId);
            return true;
        }

        return false;
    }

    // What a model reference can do when the catalogue does not say; the AI module's catalogue falls back to it.
    public static Abilities DefaultCapabilitiesFor(ModelRef reference, string? rawHint = null) => reference switch
    {
        Catalogued c => c.Model.Can,
        Tag t when t.Provider is AiProvider.Anthropic or AiProvider.OpenAI or AiProvider.Google
            => Abilities.Text | Abilities.Tools | Abilities.Vision,
        Tag when TryValidateRaw(rawHint, out var clean) && TryInferLegacy(StripPresetPrefix(clean), out _, out _, out var caps)
            => caps,
        _ => Abilities.Text | Abilities.Tools,
    };

    private static bool TryValidateRaw(string? raw, out string trimmed)
    {
        trimmed = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var candidate = raw.Trim();
        if (candidate.Length > 200 || candidate.Any(char.IsControl) || candidate.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        trimmed = candidate;
        return true;
    }

    private static string StripPresetPrefix(string value)
        => value.StartsWith("preset:", StringComparison.OrdinalIgnoreCase)
            ? value["preset:".Length..].Trim()
            : value;

    private static bool TryMatchProfile(string candidate, IReadOnlySet<string>? profileNames, [NotNullWhen(true)] out string? matched)
    {
        matched = null;
        if (string.IsNullOrWhiteSpace(candidate) || profileNames is null || profileNames.Count == 0)
        {
            return false;
        }

        foreach (var name in profileNames)
        {
            if (string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase))
            {
                matched = name;
                return true;
            }
        }

        return false;
    }

    private static bool TryParseExplicitProviderTag(
        string trimmed,
        out AiProvider provider,
        out string modelId,
        out Abilities capabilities)
    {
        capabilities = Abilities.Text | Abilities.Tools;
        if (trimmed.StartsWith("ollama/", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("ollama:", StringComparison.OrdinalIgnoreCase))
        {
            var tag = trimmed[7..].Trim();
            if (tag.Length > 0 && !tag.Any(char.IsWhiteSpace))
            {
                provider = AiProvider.Ollama;
                modelId = tag;
                return true;
            }
        }

        if (trimmed.StartsWith("openrouter/", StringComparison.OrdinalIgnoreCase))
        {
            var route = trimmed["openrouter/".Length..].Trim();
            if (route.Length > 0 && !route.Any(char.IsWhiteSpace))
            {
                provider = AiProvider.OpenRouter;
                modelId = route;
                return true;
            }
        }

        if (TrySplitSingleSegment(trimmed, "anthropic/", out var anthropicId))
        {
            provider = AiProvider.Anthropic;
            modelId = anthropicId;
            capabilities = Abilities.Text | Abilities.Tools | Abilities.Vision;
            return true;
        }

        if (TrySplitSingleSegment(trimmed, "openai/", out var openaiId))
        {
            provider = AiProvider.OpenAI;
            modelId = openaiId;
            capabilities = Abilities.Text | Abilities.Tools | Abilities.Vision;
            return true;
        }

        if (TrySplitSingleSegment(trimmed, "google/", out var googleId))
        {
            provider = AiProvider.Google;
            modelId = googleId;
            capabilities = Abilities.Text | Abilities.Tools | Abilities.Vision;
            return true;
        }

        if (TrySplitSingleSegment(trimmed, "xai/", out var xaiId))
        {
            provider = AiProvider.XAI;
            modelId = xaiId;
            return true;
        }

        provider = default;
        modelId = string.Empty;
        return false;
    }

    private static bool TrySplitSingleSegment(string trimmed, string prefix, out string id)
    {
        id = string.Empty;
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = trimmed[prefix.Length..].Trim();
        if (rest.Length == 0 || rest.Contains('/', StringComparison.Ordinal) || rest.Any(char.IsWhiteSpace))
        {
            return false;
        }

        id = rest;
        return true;
    }

    private static bool TryInferLegacy(
        string trimmed,
        out AiProvider provider,
        out string modelId,
        out Abilities capabilities)
    {
        capabilities = Abilities.Text | Abilities.Tools;
        if (TryParseExplicitProviderTag(trimmed, out provider, out modelId, out capabilities))
        {
            return true;
        }

        if (trimmed.StartsWith("claude-", StringComparison.OrdinalIgnoreCase))
        {
            provider = AiProvider.Anthropic;
            modelId = trimmed;
            capabilities = Abilities.Text | Abilities.Tools | Abilities.Vision;
            return true;
        }

        if (trimmed.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("o1-", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("o3-", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("o4-", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "o1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "o3", StringComparison.OrdinalIgnoreCase))
        {
            provider = AiProvider.OpenAI;
            modelId = trimmed;
            capabilities = Abilities.Text | Abilities.Tools | Abilities.Vision;
            return true;
        }

        if (trimmed.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase))
        {
            provider = AiProvider.Google;
            modelId = trimmed;
            capabilities = Abilities.Text | Abilities.Tools | Abilities.Vision;
            return true;
        }

        if (trimmed.StartsWith("grok-", StringComparison.OrdinalIgnoreCase))
        {
            provider = AiProvider.XAI;
            modelId = trimmed;
            return true;
        }

        if (trimmed.Contains('/', StringComparison.Ordinal))
        {
            provider = AiProvider.OpenRouter;
            modelId = trimmed;
            return true;
        }

        if (trimmed.Contains(':', StringComparison.Ordinal)
            || trimmed.StartsWith("qwen", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("gemma", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("llama", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("mistral", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("deepseek", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("phi", StringComparison.OrdinalIgnoreCase))
        {
            provider = AiProvider.Ollama;
            modelId = trimmed;
            return true;
        }

        provider = default;
        modelId = string.Empty;
        return false;
    }
}
