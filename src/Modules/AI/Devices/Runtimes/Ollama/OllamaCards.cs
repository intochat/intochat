using System.Globalization;
using IntoChat.OS.Modules.AI.Models;
using OllamaSharp.Models;

namespace IntoChat.OS.Modules.Devices.Runtimes.Ollama;

internal static class OllamaCards
{
    internal static ModelCard? FindCatalogued(string tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName))
        {
            return null;
        }

        if (KnownModels.Find(tagName) is { } exact)
        {
            return exact;
        }

        const string latestSuffix = ":latest";
        if (tagName.EndsWith(latestSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return KnownModels.Find(tagName[..^latestSuffix.Length]);
        }

        return null;
    }

    internal static ModelCard FromTag(Model tag, ShowModelResponse? show)
    {
        ArgumentNullException.ThrowIfNull(tag);

        var bytes = tag.Size > 0 ? tag.Size : (long?)null;
        if (FindCatalogued(tag.Name) is { } catalogued)
        {
            return bytes is not null ? catalogued with { Bytes = bytes } : catalogued;
        }

        var details = show?.Details ?? tag.Details;
        var rawFamily = !string.IsNullOrWhiteSpace(details?.Family)
            ? details.Family
            : BaseName(tag.Name);
        var family = TitleCase(rawFamily);
        var paramSize = details?.ParameterSize?.Trim();
        var displayName = string.IsNullOrWhiteSpace(paramSize) ? family : $"{family} {paramSize}";

        return new ModelCard(
            Id: new ModelId(tag.Name),
            Provider: AiProvider.Ollama,
            Family: family,
            Name: displayName,
            Where: Where.Device,
            Can: MapCapabilities(show?.Capabilities),
            Tier: TierFromParameterSize(paramSize),
            Price: null,
            Bytes: bytes);
    }

    internal static Abilities MapCapabilities(string[]? capabilities)
    {
        if (capabilities is null || capabilities.Length == 0)
        {
            return Abilities.Text;
        }

        var abilities = Abilities.None;
        foreach (var raw in capabilities)
        {
            abilities |= raw?.Trim().ToLowerInvariant() switch
            {
                "completion" => Abilities.Text,
                "tools" => Abilities.Tools,
                "vision" => Abilities.Vision,
                "thinking" => Abilities.Reasoning,
                "embedding" => Abilities.Embed,
                _ => Abilities.None,
            };
        }

        return abilities == Abilities.None ? Abilities.Text : abilities;
    }

    internal static Tier TierFromParameterSize(string? parameterSize)
    {
        if (string.IsNullOrWhiteSpace(parameterSize))
        {
            return Tier.Balanced;
        }

        var text = parameterSize.Trim().ToUpperInvariant();
        double multiplier;
        string digits;
        if (text.EndsWith('B'))
        {
            multiplier = 1d;
            digits = text[..^1];
        }
        else if (text.EndsWith('M'))
        {
            multiplier = 0.001d;
            digits = text[..^1];
        }
        else
        {
            return Tier.Balanced;
        }

        if (!double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || number <= 0)
        {
            return Tier.Balanced;
        }

        var billions = number * multiplier;
        if (billions < 8d)
        {
            return Tier.Fast;
        }
        if (billions < 30d)
        {
            return Tier.Balanced;
        }
        return Tier.Deep;
    }

    private static string BaseName(string tagName)
    {
        var colon = tagName.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 ? tagName[..colon] : tagName;
    }

    private static string TitleCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Ollama";
        }
        var trimmed = value.Trim();
        return char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
    }
}
