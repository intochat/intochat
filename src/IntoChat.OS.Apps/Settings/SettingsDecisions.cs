using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Settings;

internal static class SettingsDecisions
{
    public static string NormalizeSection(string? section) => (section ?? "general").Trim().ToLowerInvariant() switch
    {
        "appearance" or "profile" => "general",
        "compute" => "billing",
        "circles" or "friends" => "team",
        "modules" => "apps",
        "archived" => "files",
        var s when SettingsApp.SearchIndex.Any(entry => entry.SectionId == s) => s,
        _ => "general",
    };

    public static IReadOnlyList<SettingsSearchEntry> FilterSections(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return SettingsApp.SearchIndex;
        }

        var q = query.Trim();
        return
        [
            .. SettingsApp.SearchIndex.Where(entry =>
                entry.SectionId.Contains(q, StringComparison.OrdinalIgnoreCase)
                || entry.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                || entry.Keywords.Contains(q, StringComparison.OrdinalIgnoreCase)),
        ];
    }

    public static SettingsAppState DecideNavigate(
        SettingsAppState current,
        string? rawSection,
        string? rawSubPage,
        string? rawSheet,
        string? searchQuery)
    {
        if (!string.IsNullOrWhiteSpace(rawSection)
            && rawSection.Contains(':', StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(rawSubPage)
            && string.IsNullOrWhiteSpace(rawSheet))
        {
            var segments = rawSection.Split(':', 3, StringSplitOptions.TrimEntries);
            rawSection = segments[0];
            rawSubPage = segments.Length > 1 && !string.IsNullOrWhiteSpace(segments[1]) ? segments[1] : null;
            rawSheet = segments.Length > 2 && !string.IsNullOrWhiteSpace(segments[2]) ? segments[2] : null;
        }

        var targetSection = string.IsNullOrWhiteSpace(rawSection) ? current.Section : NormalizeSection(rawSection);
        var targetSubPage = string.IsNullOrWhiteSpace(rawSubPage) ? null : rawSubPage.Trim();
        var targetSheet = string.IsNullOrWhiteSpace(rawSheet) ? null : rawSheet.Trim();

        var history = (current.History ?? []).ToList();
        var currentPath = string.IsNullOrEmpty(current.SubPage) ? current.Section : $"{current.Section}/{current.SubPage}";
        var nextPath = string.IsNullOrEmpty(targetSubPage) ? targetSection : $"{targetSection}/{targetSubPage}";
        if (currentPath != nextPath && !string.IsNullOrEmpty(currentPath))
        {
            history.Add(currentPath);
            if (history.Count > 20)
            {
                history.RemoveAt(0);
            }
        }

        return current with
        {
            Title = $"Settings · {targetSection}",
            Status = $"Navigated to {targetSection}" + (targetSubPage != null ? $" / {targetSubPage}" : ""),
            Section = targetSection,
            SubPage = targetSubPage,
            Sheet = targetSheet,
            SearchQuery = searchQuery ?? current.SearchQuery,
            History = history,
        };
    }

    public static SettingsAppState DecideGoBack(SettingsAppState current)
    {
        var history = (current.History ?? []).ToList();
        if (history.Count == 0)
        {
            return current with { Sheet = null, SubPage = null };
        }

        var last = history[^1];
        history.RemoveAt(history.Count - 1);
        var parts = last.Split('/', 2);
        var (section, subPage) = (parts[0], parts.Length > 1 ? parts[1] : null);
        return current with
        {
            Title = $"Settings · {section}",
            Status = $"Back to {section}" + (subPage != null ? $" / {subPage}" : ""),
            Section = section,
            SubPage = subPage,
            Sheet = null,
            History = history,
        };
    }

    public static SettingsAppState DecideRefreshConnections(SettingsAppState current, string? connectionKey, string? friendApp)
    {
        var keys = (current.ConnectionKeys ?? SettingsAppState.DefaultConnectionKeys).ToList();
        if (!string.IsNullOrWhiteSpace(connectionKey) && !keys.Contains(connectionKey, StringComparer.Ordinal))
        {
            keys.Add(connectionKey);
        }

        var grants = (current.FriendGrants ?? SettingsAppState.DefaultFriendGrants).ToList();
        if (!string.IsNullOrWhiteSpace(friendApp))
        {
            var grantEntry = $"sdk -> {friendApp} ({connectionKey ?? "storage@1"})";
            if (!grants.Contains(grantEntry, StringComparer.Ordinal))
            {
                grants.Add(grantEntry);
            }
        }

        return current with
        {
            Title = "Settings",
            Status = "Connections",
            ConnectionKeys = keys,
            FriendGrants = grants,
        };
    }

    public static SettingsAppState DecideApplyUserSettings(
        SettingsAppState current,
        UserSettingsState user,
        string status,
        IReadOnlyList<string>? deployedUnits = null,
        IReadOnlyList<ProfileFriend>? teamFriends = null,
        string? section = null,
        string? archiveJson = null)
    {
        return current with
        {
            Title = "Settings",
            Status = status,
            DisplayName = user.DisplayName,
            Theme = user.Theme,
            DefaultModel = string.IsNullOrWhiteSpace(user.DefaultModel) ? current.DefaultModel : user.DefaultModel,
            Section = section ?? current.Section,
            StylePreset = user.StylePreset,
            BarLayout = user.BarLayout,
            ThemeMode = user.ThemeMode,
            DesktopTone = user.DesktopTone,
            Language = user.Language,
            TimeZone = user.TimeZone,
            DateFormat = user.DateFormat,
            ExportedArchiveJson = archiveJson ?? current.ExportedArchiveJson,
            Variables = user.Variables ?? [],
            DeployedUnits = deployedUnits ?? current.DeployedUnits ?? [],
            TeamFriends = teamFriends ?? current.TeamFriends ?? [],
        };
    }

    public static SettingsAppState DecideSetVariable(SettingsAppState current, string key, IReadOnlyList<UserVariableEntry> updatedVariables)
    {
        return current with
        {
            Section = "variables",
            Status = $"Saved variable {key}.",
            Variables = updatedVariables,
        };
    }

    public static SettingsAppState DecideSetBudget(SettingsAppState current, string computeBudgetUsd)
    {
        return current with
        {
            Section = "billing",
            ComputeBudgetUsd = computeBudgetUsd,
            Status = $"Updated monthly compute cap to {computeBudgetUsd}.",
        };
    }

    public static SettingsAppState DecideExportBrain(SettingsAppState current, string archiveJson, int unitCount)
    {
        return current with
        {
            Section = "danger",
            Sheet = "export-archive",
            Status = $"Exported portable brain archive ({unitCount} unit(s)).",
            ExportedArchiveJson = archiveJson,
        };
    }

    public static bool IsSecretKey(string key) =>
        key.EndsWith("_KEY", StringComparison.OrdinalIgnoreCase)
        || key.EndsWith("_SECRET", StringComparison.OrdinalIgnoreCase)
        || key.EndsWith("_TOKEN", StringComparison.OrdinalIgnoreCase)
        || key.EndsWith("_PASSWORD", StringComparison.OrdinalIgnoreCase);

    public static string RedactSecretDisplay(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Not set";
        }

        if (IsSecretKey(key))
        {
            return "Configured";
        }

        return value;
    }
}
