using DigitalBrain;
using DigitalBrain.UI;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.UI;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.Apps.Ino;

/// <summary>
/// Renders the persistent 264px Arc left sidebar, trust posture header badges,
/// and live interactive Pinned Apps drawer body for Unified Messenger (<see cref="InoApp"/>).
/// </summary>
public static class InoArcSidebar
{
    public const double SidebarWidth = 264;
    public const string DrawerTabPinnedApps = "pinned-apps";
    public const string DrawerTabPinnedAppsKey = "ino:drawer-tab-pinned-apps";
    public const string SidebarToggleKey = "ino:sidebar-toggle";
    public const string NewSplitMenuKey = "ino:new-split-menu";
    public const string HistoryToggleKey = "ino:history-toggle";
    public const string ClearTodayKey = "ino:clear-today";
    public const string OpenPinnedAppsKey = "ino:open-pinned-apps";

    public static Node RenderSidebar(
        InoAppState s,
        ThreadShelf shelf,
        ThreadPage? archived,
        string currentId)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(shelf);

        var items = new List<Node>
        {
            HStack(Space.Xs)[
                GhostButton("Hide", "panel-left-close")
                    .Small()
                    .Key(SidebarToggleKey)
                    .Sends(new InoToggleSidebar())
                    .ToNode(),
                PrimaryButton("+ New", Icons.Plus)
                    .Small()
                    .Style(OsMixStyles.PrimaryButton(small: true))
                    .Key(NewSplitMenuKey)
                    .Sends(new InoToggleNewMenu())
                    .ToNode()
            ],
        };

        if (s.NewMenuOpen)
        {
            items.Add(RenderNewSplitMenu());
        }

        // 1. Favourites 4-tile grid (@Ino / top Bots / close Friends)
        items.Add(RenderFavouritesGrid(shelf, currentId));

        // 2. Pinned (non-foldered pinned threads)
        var pinnedLoose = shelf.Pinned
            .Where(static e => string.IsNullOrWhiteSpace(e.Folder) && !e.IsFavourite)
            .ToArray();
        if (pinnedLoose.Length > 0)
        {
            items.Add(MutedText("Pinned"));
            foreach (var entry in pinnedLoose)
            {
                items.Add(RenderThreadRow(entry, currentId, "pinned"));
            }
        }

        // 3. Squads & Projects folders
        var folderedEntries = shelf.Entries
            .Where(static e => !string.IsNullOrWhiteSpace(e.Folder))
            .GroupBy(static e => e.Folder!, StringComparer.Ordinal)
            .OrderBy(static g => g.Key, StringComparer.Ordinal)
            .ToArray();
        var unfolderedSquads = shelf.Entries
            .Where(static e => e.Kind == ThreadKind.Squad && string.IsNullOrWhiteSpace(e.Folder) && !e.IsFavourite && !e.IsPinned)
            .ToArray();

        if (folderedEntries.Length > 0 || unfolderedSquads.Length > 0)
        {
            items.Add(MutedText("Squads & Projects"));
            foreach (var group in folderedEntries)
            {
                items.Add(HStack(Space.Xs)[
                    Text(group.Key).Strong(),
                    MutedText($"{group.Count()}")
                ]);
                foreach (var entry in group)
                {
                    items.Add(RenderThreadRow(entry, currentId, "folder"));
                }
            }

            foreach (var squad in unfolderedSquads)
            {
                items.Add(RenderThreadRow(squad, currentId, "squad"));
            }
        }

        // 4. People & Rooms (CrossBrainShared DMs & Rooms)
        var peopleAndRooms = shelf.Entries
            .Where(static e => e.Kind is ThreadKind.DirectPerson or ThreadKind.SharedRoom && !e.IsFavourite && !e.IsPinned && string.IsNullOrWhiteSpace(e.Folder))
            .ToArray();
        if (peopleAndRooms.Length > 0)
        {
            items.Add(MutedText("People & Rooms"));
            foreach (var room in peopleAndRooms)
            {
                items.Add(RenderThreadRow(room, currentId, "room"));
            }
        }

        items.Add(Divider());

        // 5. Today (unpinned DirectAssistant sessions + Clear)
        var today = shelf.Entries
            .Where(static e => e.Kind == ThreadKind.DirectAssistant && !e.IsFavourite && !e.IsPinned && string.IsNullOrWhiteSpace(e.Folder))
            .ToArray();
        items.Add(HStack(Space.Xs)[
            MutedText("Today"),
            today.Length > 0
                ? GhostButton("Clear")
                    .Small()
                    .Key(ClearTodayKey)
                    .Sends(new InoClearToday())
                    .ToNode()
                : null
        ]);
        if (today.Length == 0)
        {
            items.Add(MutedText("No open chats today."));
        }
        else
        {
            foreach (var entry in today.Take(20))
            {
                items.Add(RenderThreadRow(entry, currentId, "today"));
            }
        }

        items.Add(Divider());

        // 6. History footer
        var archivedCount = archived?.Total ?? 0;
        items.Add(NavItem(
            HistoryToggleKey,
            archivedCount > 0 ? $"History ({archivedCount})" : "History",
            "archive",
            "toggle-history",
            selected: s.HistoryOpen,
            badge: archivedCount > 0 ? archivedCount.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
            command: Sends(new InoToggleHistory())));

        if (s.HistoryOpen && archived is { Entries.Length: > 0 })
        {
            foreach (var entry in archived.Entries.Take(12))
            {
                items.Add(HStack(Space.Xs)[
                    MutedText(DisplayTitle(entry)),
                    GhostButton("Restore", "rotate-ccw")
                        .Small()
                        .Key($"ino:restore:{entry.Id}")
                        .Sends(new InoRestoreThread(entry.Id))
                        .ToNode()
                ]);
            }
        }

        return Surface(SurfaceKind.Acrylic)
            .Style(Mix.Style()
                .Tone(UiTone.Subtle)
                .Padding(10)
                .Radius(14))[
            VStack(Space.Xs, [.. items])
        ];
    }

    private static Node RenderNewSplitMenu() =>
        Surface(SurfaceKind.Elevated)
            .Style(Mix.Style().Tone(UiTone.Elevated).Padding(8).Radius(12))[
            VStack(Space.Xxs)[
                Button("New chat", Icons.Plus)
                    .Small()
                    .Key(InoView.NewKey)
                    .Sends(new InoNew())
                    .ToNode(),
                Button("New squad", "users")
                    .Small()
                    .Key("ino:menu-new-squad")
                    .Sends(new InoCreateSquad("New Squad", ["ino"]))
                    .ToNode(),
                Button("New room / DM", "message-square-plus")
                    .Small()
                    .Key("ino:menu-new-room")
                    .Sends(new InoCreateRoom("Shared Room", "SharedRoom"))
                    .ToNode(),
                Button("New specialist", Icons.Sparkles)
                    .Small()
                    .Key("ino:new-specialist")
                    .Sends(new InoToggleSpecialistPanel("new"))
                    .ToNode(),
                Button("Import SKILL.md", "upload")
                    .Small()
                    .Key("ino:import-skill")
                    .Sends(new InoToggleSpecialistPanel("import"))
                    .ToNode()
            ]
        ];

    private static Node RenderFavouritesGrid(ThreadShelf shelf, string currentId)
    {
        var favs = shelf.Favourites.Take(4).ToList();
        var tiles = new List<Node>(4);
        foreach (var fav in favs)
        {
            var isCurrent = string.Equals(fav.Id, currentId, StringComparison.Ordinal);
            var tileLabel = CompactTileLabel(DisplayTitle(fav));
            tiles.Add((isCurrent ? PrimaryButton(tileLabel, IconFor(fav)) : Button(tileLabel, IconFor(fav)))
                .Small()
                .Key($"fav-{fav.Id}")
                .Style(OsMixStyles.FilterPill(selected: isCurrent))
                .Sends(new InoSwitchConversation(fav.Id))
                .ToNode());
        }

        if (tiles.Count == 0)
        {
            tiles.Add(Button("@Ino", Icons.Sparkles)
                .Small()
                .Key("fav-ino-default")
                .Style(OsMixStyles.FilterPill(selected: string.Equals(currentId, "main", StringComparison.Ordinal)))
                .Sends(new InoSwitchConversation("main"))
                .ToNode());
        }

        return VStack(Space.Xxs)[
            MutedText("Favourites"),
            new Grid(2, [.. tiles], Gap: 6)
        ];
    }

    private static Node RenderThreadRow(ThreadEntry entry, string currentId, string section)
    {
        var isCurrent = string.Equals(entry.Id, currentId, StringComparison.Ordinal);
        var badge = entry.UnreadCount > 0
            ? entry.UnreadCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : isCurrent
                ? "Current"
                : null;
        return NavItem(
            $"session-{section}-{entry.Id}",
            DisplayTitle(entry),
            IconFor(entry),
            "switch-conversation",
            selected: isCurrent,
            badge: badge,
            command: Sends(new InoSwitchConversation(entry.Id)));
    }

    public static Node RenderTrustHeader(InoAppState s, ConversationView? view)
    {
        ArgumentNullException.ThrowIfNull(s);
        var items = new List<Node>();

        if (s.SidebarCollapsed)
        {
            items.Add(GhostButton("Sidebar", "panel-left-open")
                .Small()
                .Key(SidebarToggleKey)
                .Sends(new InoToggleSidebar())
                .ToNode());
        }

        var isShared = view is { TrustPosture: ThreadTrustPosture.CrossBrainShared };
        var isSquad = view is { Kind: ThreadKind.Squad };

        if (isShared)
        {
            items.Add(StatusPill(StatusKind.Done, "CrossBrainShared · E2EE"));
            items.Add(StatusPill(StatusKind.Idle, SharedRoomAgentPolicy.DefaultRoomDisplayName));
            var pinnedCount = view?.PinnedScreens.Length ?? 0;
            items.Add(Button(pinnedCount > 0 ? $"Pinned Apps ({pinnedCount})" : "Pinned Apps", "pin")
                .Small()
                .Key(OpenPinnedAppsKey)
                .Sends(new InoOpenPinnedApps())
                .ToNode());
        }
        else if (isSquad)
        {
            items.Add(StatusPill(StatusKind.Done, "OwnerTrusted · Squad"));
        }
        else
        {
            items.Add(StatusPill(StatusKind.Idle, "OwnerTrusted · Private"));
        }

        return HStack(Space.Xs, [.. items]);
    }

    public static Node RenderPinnedAppsDrawerBody(InoAppState s, ConversationView? view, string? hostBrainId)
    {
        ArgumentNullException.ThrowIfNull(s);
        var pinned = view?.PinnedScreens ?? [];
        if (pinned.Length == 0)
        {
            return Card("Pinned Apps", "No [Shareable] mini-apps are pinned in this room yet.")
                .Style(OsMixStyles.Card())[
                MutedText("Pin a [Shareable] mini-app like Shared Checklist to collaborate live with everyone seated in this room.")
            ];
        }

        var cards = new List<Node>(pinned.Length);
        foreach (var screen in pinned)
        {
            var viewTarget = BuildPinnedScreenViewTarget(screen, hostBrainId);
            var liveSlot = viewTarget is not null ? InoCards.ToLiveSlot(viewTarget) : null;
            cards.Add(Card(
                HumanizeScreenType(screen.ScreenType),
                $"Live [Shareable] mini-app · {screen.ScreenType}/{screen.ScreenKey}")
                .Style(OsMixStyles.Card())[
                (Node?)liveSlot ?? MutedText($"Live view unavailable for {screen.ScreenType}/{screen.ScreenKey}.")
            ]);
        }

        return VStack(Space.Sm, [.. cards]);
    }

    internal static string? BuildPinnedScreenViewTarget(PinnedScreen screen, string? hostBrainId)
    {
        if (string.IsNullOrWhiteSpace(hostBrainId) || !BrainId.IsValid(hostBrainId.Trim()))
        {
            return null;
        }
        var brainId = new BrainId(hostBrainId.Trim());
        var slug = screen.ScreenType.StartsWith("app.", StringComparison.Ordinal)
            ? screen.ScreenType["app.".Length..]
            : screen.ScreenType;
        var appRef = IntoChat.OS.Host.Primitives.AppRef.Of(brainId, slug);
        return string.IsNullOrWhiteSpace(screen.ScreenKey) || string.Equals(screen.ScreenKey, "default", StringComparison.Ordinal)
            ? appRef.ViewKey
            : $"{appRef.Key}/{screen.ScreenKey}/view";
    }

    private static string HumanizeScreenType(string screenType) => screenType switch
    {
        "room.checklist" => "Shared Checklist",
        _ => screenType,
    };

    private static string DisplayTitle(ThreadEntry entry)
        => string.IsNullOrWhiteSpace(entry.Title)
            ? $"Chat · {entry.Id[..Math.Min(8, entry.Id.Length)]}"
            : entry.Title.Trim();

    private static string CompactTileLabel(string title)
        => title.Length <= 14 ? title : title[..13] + "…";

    private static string IconFor(ThreadEntry entry) => entry.Kind switch
    {
        ThreadKind.Squad => "users",
        ThreadKind.DirectPerson => "user",
        ThreadKind.SharedRoom => "messages-square",
        _ => Icons.Sparkles,
    };
}
