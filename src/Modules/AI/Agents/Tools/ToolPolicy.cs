using System.Text.Json;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Workspace;

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
    /// Normalizes an app reference (<c>"{brain}/apps/{id}"</c>, <c>"apps/{id}"</c>, <c>"app.{id}"</c>, or <c>"{id}"</c>)
    /// into its canonical bare app ID. Returns an empty string when blank.
    /// </summary>
    public static string NormalizeAppId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var value = raw.Trim();
        if (IntoChat.OS.Host.Primitives.AppRef.TryParse(value, out var appRef))
        {
            value = appRef.Name;
        }
        else if (value.StartsWith("apps/", StringComparison.OrdinalIgnoreCase))
        {
            value = value["apps/".Length..];
        }

        if (value.StartsWith("app.", StringComparison.OrdinalIgnoreCase))
        {
            value = value["app.".Length..];
        }

        return value.Trim();
    }

    /// <summary>
    /// Extracts the distinct, ordered normalized app IDs currently open in a <see cref="WorkspaceState"/>.
    /// </summary>
    public static IReadOnlyList<string> OpenAppsFromWorkspace(WorkspaceState? workspace)
    {
        if (workspace is null)
        {
            return [];
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static void Add(List<string> target, HashSet<string> seenSet, string? candidate)
        {
            var normalized = NormalizeAppId(candidate);
            if (normalized.Length > 0 && seenSet.Add(normalized))
            {
                target.Add(normalized);
            }
        }

        foreach (var view in workspace.OpenViews ?? [])
        {
            if (view is not null)
            {
                Add(result, seen, view.App);
            }
        }

        foreach (var window in workspace.Windows ?? [])
        {
            if (window?.Tabs is null)
            {
                continue;
            }

            foreach (var tab in window.Tabs)
            {
                if (tab is not null)
                {
                    Add(result, seen, tab.App);
                }
            }
        }

        foreach (var open in workspace.Open ?? [])
        {
            Add(result, seen, open);
        }

        return result;
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
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static void Append(List<string> target, HashSet<string> seenSet, IReadOnlyList<string>? items)
        {
            if (items is null) { return; }
            foreach (var raw in items)
            {
                var normalized = NormalizeAppId(raw);
                if (normalized.Length > 0 && seenSet.Add(normalized))
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

    /// <summary>
    /// Extracts a normalized app ID when a launch tool (e.g. <c>*_launch</c>) is invoked with an <c>appId</c> or <c>app</c> argument.
    /// </summary>
    public static string? TryExtractLaunchedAppId(string toolName, IDictionary<string, object?>? arguments)
    {
        if (string.IsNullOrWhiteSpace(toolName)
            || !toolName.EndsWith("_launch", StringComparison.OrdinalIgnoreCase)
            || arguments is null
            || arguments.Count == 0)
        {
            return null;
        }

        foreach (var key in new[] { "appId", "app" })
        {
            foreach (var entry in arguments)
            {
                if (!string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase) || entry.Value is null)
                {
                    continue;
                }

                var raw = entry.Value switch
                {
                    string text => text,
                    JsonElement { ValueKind: JsonValueKind.String } el => el.GetString(),
                    _ => null,
                };

                var normalized = NormalizeAppId(raw);
                if (normalized.Length > 0)
                {
                    return normalized;
                }
            }
        }

        return null;
    }
}
