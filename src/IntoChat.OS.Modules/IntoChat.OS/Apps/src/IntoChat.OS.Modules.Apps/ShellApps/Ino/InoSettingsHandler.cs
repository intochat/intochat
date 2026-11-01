using IntoChat.OS.Modules.AI.Memory;

namespace IntoChat.OS.Modules.Apps;

internal static class InoSettingsHandler
{
    public static bool TryResolveSettingsNavigation(
        string text,
        out string section,
        out string? subPage,
        out string? sheet,
        out bool requiresApproval)
    {
        section = "general";
        subPage = null;
        sheet = null;
        requiresApproval = false;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var isDestructive = text.Contains("revoke", StringComparison.OrdinalIgnoreCase)
            || text.Contains("delete account", StringComparison.OrdinalIgnoreCase)
            || text.Contains("clear memory", StringComparison.OrdinalIgnoreCase)
            || text.Contains("clear ino memory", StringComparison.OrdinalIgnoreCase)
            || text.Contains("wipe memory", StringComparison.OrdinalIgnoreCase);

        if (isDestructive)
        {
            requiresApproval = true;
            if (text.Contains("key", StringComparison.OrdinalIgnoreCase))
            {
                section = "keys";
                return true;
            }
            if (text.Contains("memory", StringComparison.OrdinalIgnoreCase))
            {
                section = "files";
                subPage = "memory";
                return true;
            }
            if (text.Contains("account", StringComparison.OrdinalIgnoreCase))
            {
                section = "danger";
                sheet = "delete-account";
                return true;
            }
        }

        if (text.Contains("local ollama", StringComparison.OrdinalIgnoreCase)
            || text.Contains("ollama server", StringComparison.OrdinalIgnoreCase)
            || text.Contains("local server", StringComparison.OrdinalIgnoreCase)
            || (text.Contains("ollama", StringComparison.OrdinalIgnoreCase)
                && (text.Contains("where", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("set up", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("setup", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("configure", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("settings", StringComparison.OrdinalIgnoreCase))))
        {
            section = "models";
            subPage = "local-server";
            return true;
        }

        var mentionsConnectionIntent = text.Contains("connection", StringComparison.OrdinalIgnoreCase)
            || text.Contains("sheet", StringComparison.OrdinalIgnoreCase)
            || text.Contains("oauth", StringComparison.OrdinalIgnoreCase)
            || (text.Contains("connect", StringComparison.OrdinalIgnoreCase) && text.Contains("settings", StringComparison.OrdinalIgnoreCase));

        if (mentionsConnectionIntent)
        {
            section = "connections";
            if (text.Contains("google drive", StringComparison.OrdinalIgnoreCase) || text.Contains("google_drive", StringComparison.OrdinalIgnoreCase))
            {
                subPage = "google_drive";
                if (text.Contains("sheet", StringComparison.OrdinalIgnoreCase)) { sheet = "google_drive"; }
                return true;
            }
            if (text.Contains("github", StringComparison.OrdinalIgnoreCase))
            {
                subPage = "github";
                if (text.Contains("sheet", StringComparison.OrdinalIgnoreCase)) { sheet = "github"; }
                return true;
            }
            if (text.Contains("slack", StringComparison.OrdinalIgnoreCase))
            {
                subPage = "slack";
                if (text.Contains("sheet", StringComparison.OrdinalIgnoreCase)) { sheet = "slack"; }
                return true;
            }
            if (text.Contains("notion", StringComparison.OrdinalIgnoreCase))
            {
                subPage = "notion";
                if (text.Contains("sheet", StringComparison.OrdinalIgnoreCase)) { sheet = "notion"; }
                return true;
            }
        }

        var mentionsSettingsNav = text.Contains("settings", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("where do i ", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("open settings", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("navigate to settings", StringComparison.OrdinalIgnoreCase);

        if (!mentionsSettingsNav)
        {
            return false;
        }

        if (text.Contains("memory", StringComparison.OrdinalIgnoreCase))
        {
            section = "files";
            subPage = "memory";
            return true;
        }
        if (text.Contains("file", StringComparison.OrdinalIgnoreCase) || text.Contains("storage", StringComparison.OrdinalIgnoreCase))
        {
            section = "files";
            return true;
        }
        if (text.Contains("key", StringComparison.OrdinalIgnoreCase) && !text.Contains("keyboard", StringComparison.OrdinalIgnoreCase))
        {
            section = "keys";
            return true;
        }
        if (text.Contains("variable", StringComparison.OrdinalIgnoreCase))
        {
            section = "variables";
            return true;
        }
        if (text.Contains("model", StringComparison.OrdinalIgnoreCase))
        {
            section = "models";
            return true;
        }
        if (text.Contains("connection", StringComparison.OrdinalIgnoreCase))
        {
            section = "connections";
            return true;
        }
        if (text.Contains("billing", StringComparison.OrdinalIgnoreCase) || text.Contains("subscription", StringComparison.OrdinalIgnoreCase))
        {
            section = "billing";
            return true;
        }
        if (text.Contains("team", StringComparison.OrdinalIgnoreCase) || text.Contains("member", StringComparison.OrdinalIgnoreCase))
        {
            section = "team";
            return true;
        }
        if (text.Contains("notification", StringComparison.OrdinalIgnoreCase))
        {
            section = "notifications";
            return true;
        }
        if (text.Contains("keyboard", StringComparison.OrdinalIgnoreCase) || text.Contains("shortcut", StringComparison.OrdinalIgnoreCase))
        {
            section = "keyboard";
            return true;
        }
        if (text.Contains("desktop", StringComparison.OrdinalIgnoreCase) || text.Contains("wallpaper", StringComparison.OrdinalIgnoreCase))
        {
            section = "desktop";
            return true;
        }
        if (text.Contains("danger", StringComparison.OrdinalIgnoreCase))
        {
            section = "danger";
            return true;
        }
        if (text.Contains("app", StringComparison.OrdinalIgnoreCase))
        {
            section = "apps";
            return true;
        }

        return text.StartsWith("open settings", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("navigate to settings", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<InoAppState> ExecuteAsync(
        InoAppState currentState,
        string text,
        string section,
        string? subPage,
        string? sheet,
        bool requiresApproval,
        UserSettingsState settings,
        MemoryRecallHit[] recalled,
        IMemory? memory,
        Func<string, IApp?> tryGetApp,
        Action<Exception, string>? onRecoverableError = null)
    {
        var routeArg = string.IsNullOrWhiteSpace(sheet)
            ? (string.IsNullOrWhiteSpace(subPage) ? section : $"{section}:{subPage}")
            : $"{section}:{subPage ?? string.Empty}:{sheet}";

        try
        {
            if (tryGetApp("apps/settings") is { } settingsApp)
            {
                await settingsApp.Request("navigate", routeArg);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            onRecoverableError?.Invoke(error, "Settings app neuron is optional when running InoApp in standalone unit tests.");
        }

        var routeHint = $"routed:settings.navigate:{routeArg}";
        var targetLabel = string.IsNullOrWhiteSpace(subPage)
            ? section
            : $"{section} / {subPage}";
        var reply = requiresApproval
            ? $"Ino ({settings.DefaultModel}): Opened **Settings → {targetLabel}** (`neuron://local/apps/settings`). Please review and approve the destructive settings change below before I apply it."
            : $"Ino ({settings.DefaultModel}): Navigated **Settings** to **{targetLabel}** (`neuron://local/apps/settings` · `{routeArg}`).";

        if (memory is not null)
        {
            await memory.CaptureEpisode("ino.settings", $"User: {text} | Route: {routeHint}", importance: 6);
        }

        var turnsCount = currentState.Turns.Length + 1;
        var cards = currentState.ActiveCards;
        if (requiresApproval)
        {
            var cardId = $"card-{turnsCount}";
            cards =
            [
                .. cards,
                new InoCard(
                    cardId,
                    "approval",
                    $"Confirm settings action: {text}",
                    $"Target: Settings → {targetLabel} ({routeArg}). Requires explicit LFP confirmation.",
                    "neuron://local/os.settings/settings",
                    "pending",
                    $"neuron://local/apps/ino#{cardId}"),
            ];
        }
        else
        {
            var cardId = $"settings-{turnsCount}";
            cards =
            [
                .. cards,
                new InoCard(
                    cardId,
                    "settings",
                    $"Settings · {targetLabel}",
                    $"Autonomous Settings co-pilot navigated to {routeArg}.",
                    "neuron://local/apps/settings",
                    "ready",
                    $"neuron://local/apps/ino#{cardId}"),
            ];
        }

        var title = currentState.Turns.Length == 0
            ? (text.Length <= 52 ? text : text[..52])
            : currentState.Title;
        var nextTurns = currentState.Turns.Append(new InoTurn(text, reply, routeHint, recalled.Length)).ToArray();

        return currentState with
        {
            Title = title,
            Status = routeHint,
            Turns = nextTurns,
            Cards = cards,
        };
    }
}
