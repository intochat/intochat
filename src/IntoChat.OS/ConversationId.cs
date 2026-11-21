using System.Text.RegularExpressions;
using Orleans;

namespace IntoChat.OS;

/// <summary>
/// Typed identifier for a conversation. Canonical ids follow <c>c-{32hex}</c>.
/// </summary>
[GenerateSerializer, Alias("os.conversation-id")]
public readonly partial record struct ConversationId
{
    [Id(0)]
    public string Value { get; init; }

    private ConversationId(string value) => Value = value;
    public static ConversationId New() => new("c-" + Guid.NewGuid().ToString("N"));

    public bool IsCanonical => IsCanonicalValue(Value);

    public static bool IsCanonicalValue(string? value)
        => !string.IsNullOrEmpty(value) && CanonicalPattern().IsMatch(value);

    public static bool TryParse(string? value, out ConversationId id)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            id = default;
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Contains('/', StringComparison.Ordinal)
            || trimmed.Contains('\\', StringComparison.Ordinal)
            || trimmed.Contains('\0', StringComparison.Ordinal))
        {
            id = default;
            return false;
        }

        id = new ConversationId(trimmed);
        return true;
    }

    public static ConversationId Parse(string value)
        => TryParse(value, out var id)
            ? id
            : throw new ArgumentException($"Invalid conversation id '{value}'.", nameof(value));

    public static ConversationId ParseCanonical(string value)
        => IsCanonicalValue(value)
            ? new ConversationId(value)
            : throw new ArgumentException($"Conversation id '{value}' must match c-{{32hex}}.", nameof(value));

    public static ConversationId FromOrNew(string? value)
        => TryParse(value, out var id) && id.IsCanonical ? id : New();

    public override string ToString() => Value ?? string.Empty;

    [GeneratedRegex("^c-[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalPattern();
}
