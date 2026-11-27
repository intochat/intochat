using DigitalBrain.Rules;
using DigitalBrain.UI;
using IntoChat.OS.UI;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.Apps.Settings;

/// <summary>
/// Thin shell and router for the Settings screen: owns <see cref="RenderRoot"/>,
/// navigation sidebar, header/breadcrumb bar, modal sheet overlay, and bounded-context
/// section dispatch. Section implementations live in dedicated internal view classes.
/// </summary>
internal static class SettingsViewRenderer
{
    public static Node RenderRoot(SettingsAppState state, SettingsChildren children, Rejected? lastRejection = null)
    {
        var section = SettingsAppState.NormalizeSection(state.Section);
        var sectionEntry = SettingsApp.SearchIndex.First(entry => entry.SectionId == section);

        var sidebar = BuildSidebar(state, section, children);
        var contentChildren = new List<Node?>
        {
            lastRejection is not null
                ? Banner("Invalid setting value", string.Join(" ", lastRejection.Messages), tone: "error")
                : null,
            !string.IsNullOrEmpty(state.Sheet)
                ? RenderModalSheet(state, state.Sheet!)
                : null,
            RenderSectionBody(state, section, state.SubPage, children),
        };

        return AppShell(
            key: "settings-shell",
            sidebar: sidebar,
            body: VStack(Space.Md, contentChildren.ToArray()),
            toolbar: RenderHeader(state, sectionEntry),
            transition: state.ActiveTransition);
    }

    private static Node BuildSidebar(SettingsAppState state, string activeSection, SettingsChildren children)
    {
        var filtered = SettingsAppState.FilterSections(state.SearchQuery);
        var groups = new[] { "Preferences", "Capabilities & Data", "Organization & System" };
        var nodes = new List<Node?>
        {
            SettingsControls.SearchForm(children, state),
            !string.IsNullOrWhiteSpace(state.SearchQuery)
                ? GhostButton("Clear search", "x")
                    .Key("settings:clear-search")
                    .Small()
                    .Sends(new SettingsClearSearch())
                    .ToNode()
                : null,
        };

        if (filtered.Count == 0)
        {
            nodes.Add(Empty(
                "No matching sections",
                $"Nothing matched '{state.SearchQuery}'.",
                PrimaryButton("Show all sections")
                    .Key("settings:empty-reset")
                    .Sends(new SettingsClearSearch())));
            return VStack(Space.Sm, nodes.ToArray());
        }

        foreach (var group in groups)
        {
            var entries = filtered
                .Where(e => string.Equals(e.Group, group, StringComparison.Ordinal) && e.SectionId != "danger")
                .ToArray();
            if (entries.Length == 0)
            {
                continue;
            }

            nodes.Add(Divider(group));
            nodes.Add(VStack(
                Space.Xxs,
                entries.Select(entry => (Node)NavItem(
                    $"nav-{entry.SectionId}",
                    entry.Title,
                    entry.Icon,
                    $"navigate:{entry.SectionId}",
                    selected: activeSection == entry.SectionId && string.IsNullOrEmpty(state.SubPage),
                    command: Sends(new SettingsNavigate(entry.SectionId)))).ToArray()));
        }

        if (filtered.FirstOrDefault(e => e.SectionId == "danger") is { } dangerEntry)
        {
            nodes.Add(Divider());
            nodes.Add(NavItem(
                "nav-danger",
                dangerEntry.Title,
                dangerEntry.Icon,
                "navigate:danger",
                selected: activeSection == "danger",
                command: Sends(new SettingsNavigate("danger"))));
        }

        return VStack(Space.Sm, nodes.ToArray());
    }

    private static Node RenderHeader(SettingsAppState state, SettingsSearchEntry sectionEntry)
    {
        var breadcrumb = string.IsNullOrEmpty(state.SubPage)
            ? sectionEntry.Title
            : $"{sectionEntry.Title} / {state.SubPage}";

        var trailing = new List<Node?>
        {
            state.CanGoBack
                ? GhostButton("Back", "arrow-left")
                    .Key("settings:header-back")
                    .Small()
                    .Guard(state.CanReturnRule)
                    .Sends(new SettingsGoBack())
                    .ToNode()
                : null,
            StatusPill(StatusKind.Idle, sectionEntry.Group),
            StatusPill(StatusKind.Done, $"{state.StylePreset} · {state.ThemeMode}"),
        };

        return VStack(Space.Xs)[
            HeaderBar(breadcrumb, state.Status, trailing.ToArray()),
            Divider()
        ];
    }

    private static Node RenderModalSheet(SettingsAppState state, string sheet)
    {
        var detail = sheet switch
        {
            "install-link" => "Paste a verified Smart App link (https://intochat.app/a/...) to inspect its manifest and capability requirements.",
            "add-connection" or "google-drive" or "google_drive" => "Authorize an OAuth 2.0 or token-based integration through the zero-leakage Capability Broker.",
            "ollama-config" => $"Configure local Ollama or LM Studio server endpoint ({state.LocalOllamaEndpoint}) and reasoning effort.",
            "invite" => "Invite a teammate to this brain and assign Owner, Member, or Guest group permissions.",
            "export-archive" => state.ExportedArchiveJson ?? "Export a portable JSON archive of this brain's settings, profile, and deployed Synapse units.",
            "confirm-delete" => "Two-step confirmation required: deleting or resetting this brain permanently revokes all sessions.",
            _ => $"Active modal sheet: {sheet}",
        };

        return Card($"Sheet · {sheet}", "Modal configuration sheet · Dismiss with Close or Esc")
            .Style(OsMixStyles.Card(elevated: true))[
            VStack(Space.Sm)[
                Text(detail),
                HStack(Space.Sm)[
                    Button("Close sheet", "x")
                        .Key("settings:close-sheet")
                        .Small()
                        .Sends(new SettingsNavigate(state.Section, state.SubPage, ""))
                ]
            ]
        ];
    }

    private static Node RenderSectionBody(SettingsAppState state, string section, string? subPage, SettingsChildren children) => section switch
    {
        "desktop" => SettingsGeneralAndDevicesView.RenderDesktopSection(state, children),
        "apps" => SettingsWorkspaceAndSystemView.RenderAppsSection(state, subPage),
        "connections" => SettingsConnectionsAndKeysView.RenderConnectionsSection(state, subPage, children),
        "files" => SettingsConnectionsAndKeysView.RenderFilesSection(state),
        "models" => SettingsModelsAndWebView.RenderModelsSection(state, subPage, children),
        "keys" => SettingsConnectionsAndKeysView.RenderKeysSection(state, children),
        "variables" => SettingsConnectionsAndKeysView.RenderVariablesSection(state),
        "billing" => SettingsWorkspaceAndSystemView.RenderBillingSection(state),
        "team" => SettingsWorkspaceAndSystemView.RenderTeamSection(state, subPage),
        "notifications" => SettingsWorkspaceAndSystemView.RenderNotificationsSection(state, children),
        "keyboard" => SettingsWorkspaceAndSystemView.RenderKeyboardSection(),
        "danger" => SettingsWorkspaceAndSystemView.RenderDangerSection(),
        _ => SettingsGeneralAndDevicesView.RenderGeneralSection(state, subPage, children),
    };

    internal static Node DetailSubPage(
        string surfaceTitle,
        string heading,
        string subtitle,
        IReadOnlyList<IReadOnlyList<string>> rows,
        string backId,
        string backLabel) =>
        Surface(SurfaceKind.Elevated, surfaceTitle, subtitle)
            .Style(OsMixStyles.Surface(SurfaceKind.Elevated))[
            VStack(Space.Md)[
                Heading(heading, 3, subtitle),
                Table(heading, ["Property", "Value"], rows),
                HStack(Space.Sm)[
                    Button(backLabel, "arrow-left")
                        .Key(backId)
                        .Small()
                        .Sends(new SettingsGoBack())
                ]
            ]
        ];

    // The save a preference button sends: the current display name, theme and model with the given ones applied; style and bar
    // layout only when a style, bar, mode or tone is given, the mode only when a mode or tone is given.
    internal static SettingsSavePreferences PrefSave(
        SettingsAppState state,
        string? theme = null,
        string? model = null,
        string? preset = null,
        string? barLayout = null,
        string? themeMode = null,
        string? desktopTone = null)
    {
        var styled = preset != null || barLayout != null || themeMode != null || desktopTone != null;
        var moded = themeMode != null || desktopTone != null;
        return new SettingsSavePreferences(
            DisplayName: state.DisplayName,
            Theme: theme ?? state.Theme,
            DefaultModel: model ?? state.DefaultModel,
            StylePreset: styled ? preset ?? state.StylePreset : null,
            BarLayout: styled ? barLayout ?? state.BarLayout : null,
            ThemeMode: moded ? themeMode ?? state.ThemeMode : null,
            DesktopTone: desktopTone);
    }

    internal static Node PrefButton(
        SettingsAppState state,
        string id,
        string label,
        bool selected,
        string? theme = null,
        string? model = null,
        string? preset = null,
        string? barLayout = null,
        string? themeMode = null,
        string? desktopTone = null,
        string? icon = null) =>
        (selected ? PrimaryButton(label, icon) : Button(label, icon))
            .Key(id)
            .Small()
            .Style(OsMixStyles.FilterPill(selected))
            .Sends(PrefSave(state, theme, model, preset, barLayout, themeMode, desktopTone))
            .ToNode();
}
