using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Conversations;

namespace IntoChat.OS.Apps.AgentApp;

/// <summary>
/// Pure policy for pure-data agent apps: dedicated conversation/agent identity,
/// isolated <see cref="AgentDefinition"/> projection, and seating equivalence.
/// </summary>
public static class AgentAppPolicy
{
    public const string ConversationPrefix = "agent-";

    /// <summary>Returns the dedicated conversation ID for an agent app, disjoint from Ino's session IDs.</summary>
    public static ConversationId ConversationOf(string appId)
        => ConversationId.Parse(ConversationPrefix + NormalizeAppId(appId));

    /// <summary>Returns the seated agent ID for an agent app.</summary>
    public static string AgentIdOf(string appId)
        => ConversationPrefix + NormalizeAppId(appId);

    /// <summary>Projects an agent app's manifest and bound connection slots into an isolated <see cref="AgentDefinition"/>.</summary>
    public static AgentDefinition Definition(AppManifest app, IReadOnlyDictionary<string, string>? boundSlots)
    {
        ArgumentNullException.ThrowIfNull(app);
        var agent = app.Agent ?? throw new InvalidOperationException("App manifest does not declare an agent.");

        var declaredSlots = new HashSet<string>(
            (app.Slots ?? []).Where(static s => s is not null && !string.IsNullOrWhiteSpace(s.Name)).Select(static s => s.Name),
            StringComparer.Ordinal);

        var sources = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (agent.Tools is { Length: > 0 } toolSlots && boundSlots is { Count: > 0 })
        {
            foreach (var slotName in toolSlots)
            {
                if (string.IsNullOrWhiteSpace(slotName) || !declaredSlots.Contains(slotName))
                {
                    continue;
                }

                if (boundSlots.TryGetValue(slotName, out var canonicalKey)
                    && !string.IsNullOrWhiteSpace(canonicalKey)
                    && seen.Add(canonicalKey.Trim()))
                {
                    sources.Add(canonicalKey.Trim());
                }
            }
        }

        return new AgentDefinition
        {
            DisplayName = string.IsNullOrWhiteSpace(app.Title) ? "Agent" : app.Title.Trim(),
            Description = app.Description ?? string.Empty,
            Instructions = agent.Instructions,
            Model = agent.Model,
            Contracts = [],
            IncludeComponentOperations = false,
            ToolSources = [.. sources],
        };
    }

    /// <summary>Returns true when two <see cref="AgentDefinition"/> values produce the same conversation seat.</summary>
    public static bool SameSeat(AgentDefinition? seated, AgentDefinition wanted)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        if (seated is null)
        {
            return false;
        }

        return string.Equals(seated.DisplayName, wanted.DisplayName, StringComparison.Ordinal)
            && string.Equals(seated.Description, wanted.Description, StringComparison.Ordinal)
            && string.Equals(seated.Instructions, wanted.Instructions, StringComparison.Ordinal)
            && Equals(seated.Model, wanted.Model)
            && seated.IncludeComponentOperations == wanted.IncludeComponentOperations
            && (seated.Contracts ?? []).SequenceEqual(wanted.Contracts ?? [], StringComparer.Ordinal)
            && SameToolSources(seated.ToolSources, wanted.ToolSources);
    }

    private static bool SameToolSources(string[]? left, string[]? right)
        => (left is null && right is null)
            || (left is not null && right is not null && left.SequenceEqual(right, StringComparer.Ordinal));

    private static string NormalizeAppId(string appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        var normalized = appId.Trim().ToLowerInvariant();
        var chars = new char[normalized.Length];
        for (var i = 0; i < normalized.Length; i++)
        {
            var ch = normalized[i];
            chars[i] = char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-';
        }
        var cleaned = new string(chars).Trim('-');
        if (cleaned.Length == 0)
        {
            throw new ArgumentException("App id must contain at least one alphanumeric character.", nameof(appId));
        }
        return cleaned;
    }
}
