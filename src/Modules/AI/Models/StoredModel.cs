using IntoChat.OS.Modules.Devices;

namespace IntoChat.OS.Modules.AI.Models;

internal enum StoredModelKind
{
    None,
    Profile,
    Card,
    OllamaTag,
    DeviceRoute,
}

// Reads persisted or wire model strings without guessing bare uncatalogued provider prefixes.
internal static class StoredModel
{
    public static bool TryRead(string? raw, IReadOnlySet<string> profiles, out ModelSelection selection)
    {
        if (TryParse(raw, profiles, out _, out var value))
        {
            selection = new ModelSelection(value);
            return true;
        }

        selection = default!;
        return false;
    }

    internal static bool TryParse(
        string? raw,
        IReadOnlySet<string>? profiles,
        out StoredModelKind kind,
        out string value)
    {
        kind = StoredModelKind.None;
        value = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var trimmed = raw.Trim();
        if (trimmed.Length > 200 || trimmed.Any(char.IsControl) || trimmed.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        if (DeviceModelRef.TryParse(trimmed, out var deviceRef))
        {
            kind = StoredModelKind.DeviceRoute;
            value = deviceRef.ToModelId().Value;
            return true;
        }

        if (trimmed.StartsWith("profile:", StringComparison.OrdinalIgnoreCase))
        {
            var profileKey = trimmed["profile:".Length..].Trim();
            if (TryMatchProfile(profileKey, profiles, out var matched))
            {
                kind = StoredModelKind.Profile;
                value = matched;
                return true;
            }
            return false;
        }

        if (TryMatchProfile(trimmed, profiles, out var matchedProfile))
        {
            kind = StoredModelKind.Profile;
            value = matchedProfile;
            return true;
        }

        var unprefixed = trimmed.StartsWith("preset:", StringComparison.OrdinalIgnoreCase)
            ? trimmed["preset:".Length..].Trim()
            : trimmed;

        if (KnownModels.Find(unprefixed) is { } card)
        {
            kind = StoredModelKind.Card;
            value = card.Id.Value;
            return true;
        }

        if (unprefixed.StartsWith("ollama/", StringComparison.OrdinalIgnoreCase)
            || unprefixed.StartsWith("ollama:", StringComparison.OrdinalIgnoreCase))
        {
            var tag = unprefixed[7..].Trim();
            if (tag.Length > 0 && !tag.Any(char.IsWhiteSpace))
            {
                kind = StoredModelKind.OllamaTag;
                value = tag;
                return true;
            }
        }

        return false;
    }

    private static bool TryMatchProfile(string candidate, IReadOnlySet<string>? profiles, out string matched)
    {
        matched = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate) || profiles is null || profiles.Count == 0)
        {
            return false;
        }

        foreach (var name in profiles)
        {
            if (string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase))
            {
                matched = name;
                return true;
            }
        }

        return false;
    }
}
