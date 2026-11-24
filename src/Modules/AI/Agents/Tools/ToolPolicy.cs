using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Modules.AI.Agents.Tools;

/// <summary>
/// Pure tool-scoping policy for agent apps and workspace-scoped assistants.
/// </summary>
internal static class ToolPolicy
{
    /// <summary>
    /// Returns the ordered, distinct canonical connection keys bound to slots declared in <see cref="AppAgent.Tools"/>.
    /// Returns an empty array when the app is not an agent app or has no bound tool slots.
    /// </summary>
    public static string[] SourcesFor(AppManifest app, IReadOnlyDictionary<string, string>? boundSlots)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (app.Agent?.Tools is not { Length: > 0 } declaredTools || boundSlots is null || boundSlots.Count == 0)
        {
            return [];
        }

        var declaredSlots = new HashSet<string>(
            (app.Slots ?? []).Where(static s => s is not null && !string.IsNullOrWhiteSpace(s.Name)).Select(static s => s.Name),
            StringComparer.Ordinal);

        var sources = new List<string>(declaredTools.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slotName in declaredTools)
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

        return [.. sources];
    }

    /// <summary>
    /// Returns the distinct ordered app IDs whose operations a workspace assistant may expose in a turn:
    /// always-on apps plus open workspace apps plus apps opened during this conversation.
    /// </summary>
    public static IReadOnlyList<string> AppsForAssistant(
        IReadOnlyList<string>? alwaysOn,
        IReadOnlyList<string>? openApps,
        IReadOnlyList<string>? openedByConversation)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        static void Append(List<string> target, HashSet<string> seenSet, IReadOnlyList<string>? items)
        {
            if (items is null) { return; }
            foreach (var raw in items)
            {
                if (string.IsNullOrWhiteSpace(raw)) { continue; }
                var normalized = raw.Trim();
                if (seenSet.Add(normalized))
                {
                    target.Add(normalized);
                }
            }
        }

        Append(result, seen, alwaysOn);
        Append(result, seen, openApps);
        Append(result, seen, openedByConversation);
        return result;
    }
}
