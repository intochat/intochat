using IntoChat.OS.Apps;
using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Views;

namespace IntoChat.OS.Kernel.Tests.Unit;

// The desktop declares one startup operation and renders windows above a dock.
public sealed class OsDesktopAppFacts
{
    [Fact]
    public void TheDesktopDeclaresStartAndLayoutOperations()
    {
        var bundle = Assert.Single(SolutionProjects.FeaturesOf("os"), app => app.Name == "desktop");
        Assert.Equal(["start", "layout", "narrow"], bundle.Manifest.Operations.Select(operation => operation.Name));
        // The layout is the desktop's own text (apps/desktop/layout), not a setting: a configure would redeploy the desktop.
        Assert.Empty(bundle.Manifest.Settings);
        Assert.Contains("start.cs", bundle.Files.Keys);
        // One startup signal: only start.cs subscribes to `start`.
        Assert.Equal(["start.cs"], bundle.Files.Where(file => file.Value.Contains("\"start\"", StringComparison.Ordinal) && file.Key != "app.cs").Select(file => file.Key));
    }

    // Option A: the dock is at the bottom even before any window opens.
    [Fact]
    public void AnEmptyDesktopShowsNoWindowsAndTheDockAtTheBottom()
    {
        var model = new DesktopModel([], null, [new("ino", "Ino", null, true, false), new("calculator", "Calculator", null, false, false)], false, false, null);
        var tree = DesktopLayout.Render(model);
        Assert.Equal(["windows", "dock"], tree.Children.Select(node => node.Key));
        Assert.Equal([0, 68], tree.Extents ?? []);
        Assert.Empty(tree.Children[0].Children);
        var dock = tree.Children[1];
        Assert.Equal(("dock", UiNodeKind.Row), (dock.Key, dock.Kind));
        Assert.Equal("Ino", Flatten(dock).First(node => node.Kind == UiNodeKind.Button).Text);
    }

    [Fact]
    public void WindowsSitSideBySideAboveTheDock()
    {
        var tree = DesktopLayout.Render(Model(2, "b", narrow: false));
        Assert.Equal(["windows", "dock"], tree.Children.Select(node => node.Key));
        Assert.Equal([new UiChildRef(UIVocabulary.ViewportType, "brain/apps/desktop/window/a"), new UiChildRef(UIVocabulary.ViewportType, "brain/apps/desktop/window/b")],
            Flatten(tree).Where(node => node.Kind == UiNodeKind.Reference).Select(node => node.Reference));
        var chrome = Assert.Single(Flatten(tree), node => node.Key == "window-a");
        Assert.Equal((UiNodeKind.Card, "App a"), (chrome.Kind, chrome.Text));
        Assert.Contains(Flatten(chrome), node => node.Kind == UiNodeKind.Button && node.Action == "close:a");
        Assert.Contains(Flatten(chrome), node => node.Kind == UiNodeKind.Button && node.Action == "focus:a");
    }

    // An app's view fills its window: rows share the window area and each window's content takes
    // what its chrome leaves (an unbounded height breaks views that expand, such as Ino's chat).
    [Fact]
    public void EachWindowGivesItsContentABoundedArea()
    {
        var tree = DesktopLayout.Render(Model(4, "b", narrow: false));
        var windows = Assert.Single(tree.Children, node => node.Key == "windows");
        Assert.Equal(windows.Children.Select(_ => 0.0), windows.Extents ?? []);
        foreach (var id in new[] { "a", "b", "c", "d" })
        {
            var frame = Assert.Single(Flatten(tree), node => node.Key == "frame-" + id);
            Assert.Equal(["window-" + id, "content-" + id], frame.Children.Select(node => node.Key));
            Assert.Equal([DesktopLayout.ChromeHeight, 0], frame.Extents ?? []);
        }
    }

    [Fact]
    public void DockButtonsOpenTheirApp()
    {
        var tree = DesktopLayout.Render(Model(0, null, narrow: false));
        Assert.Equal("open:ino", Assert.Single(Flatten(tree), node => node.Key == "dock-ino").Action);
    }

    [Fact]
    public void SevenWindowsWrapIntoRowsWithoutThrowing()
    {
        var tree = DesktopLayout.Render(Model(7, "g", narrow: false));
        Assert.Equal(7, Flatten(tree).Count(node => node.Kind == UiNodeKind.Reference));
        var rows = Assert.Single(tree.Children, node => node.Key == "windows").Children;
        Assert.True(rows.Length > 1);
        Assert.All(rows, row => Assert.InRange(row.Children.Length, 1, DesktopLayout.PerRow));
    }

    [Fact]
    public void NarrowShowsOnlyTheFocusedWindow()
    {
        var tree = DesktopLayout.Render(Model(3, "b", narrow: true));
        var only = Assert.Single(Flatten(tree), node => node.Kind == UiNodeKind.Reference);
        Assert.Equal("brain/apps/desktop/window/b", only.Reference!.Name);
    }

    [Fact]
    public void EachWindowVerbHasItsOwnSmallHandler()
    {
        var bundle = Assert.Single(SolutionProjects.FeaturesOf("os"), app => app.Name == "desktop");
        foreach (var (file, prefix) in new[] { ("open.cs", "\"open:\""), ("close.cs", "\"close:\""), ("focus.cs", "\"focus:\"") })
        {
            Assert.Contains(prefix, bundle.Files[file], StringComparison.Ordinal);
            Assert.InRange(bundle.Files[file].Split('\n').Length, 1, 40);
        }
        Assert.Contains("WorkspaceChanged", bundle.Files["restore.cs"], StringComparison.Ordinal);
    }

    // Width is the shell's to report: no visible toggle; narrow.cs takes the shell's `narrow` request.
    [Fact]
    public void TheDockHasNoNarrowButtonAndTheShellsWidthHintIsARequest()
    {
        foreach (var narrow in new[] { false, true })
        {
            var tree = DesktopLayout.Render(Model(2, "b", narrow));
            Assert.DoesNotContain(Flatten(tree), node => node.Key == "narrow" || node.Action?.StartsWith("narrow", StringComparison.Ordinal) == true);
        }
        var bundle = Assert.Single(SolutionProjects.FeaturesOf("os"), app => app.Name == "desktop");
        Assert.Contains("AppRequested", bundle.Files["narrow.cs"], StringComparison.Ordinal);
        Assert.Contains("\"narrow\"", bundle.Files["narrow.cs"], StringComparison.Ordinal);
        Assert.InRange(bundle.Files["narrow.cs"].Split('\n').Length, 1, 40);
    }

    [Fact]
    public void HiddenAppsNeverBecomeWindows()
    {
        IntoChat.OS.Flutter.Workspace.WorkspaceViewRef[] views = [new("desktop", "brain/apps/desktop", "full", ""), new("workspace", "brain/apps/workspace", "full", ""), new("calculator", "brain/apps/calculator", "full", "")];
        Assert.Equal(["calculator"], Desktop.Windowed("brain", views).Select(view => view.Id));
    }

    [Fact]
    public void TheDockShowsInoFirstThenTheGalleryAndUsageAvatarAndMoreOnTheRight()
    {
        var model = new DesktopModel([], null, [new("ino", "Ino", null, true, false), new("notes", "Notes", null, true, false), new("calc", "Calc", null, false, true)],
            false, false, null, Usage: "$1.25", Person: "Vlad", LayoutButton: "brain/apps/desktop/classic", SettingsButton: "brain/apps/desktop/settings");
        var dock = Assert.Single(DesktopLayout.Render(model).Children, node => node.Key == "dock");
        var buttons = Flatten(dock).Where(node => node.Kind == UiNodeKind.Button).ToArray();
        Assert.Equal(["open:ino", "open:notes", "open:calc"], buttons.Take(3).Select(node => node.Action));
        Assert.Contains(buttons, node => node.Key == "gallery" && node.Text == "Apps");
        var right = Assert.Single(Flatten(dock), node => node.Key == "right");
        Assert.Equal(["usage", "avatar", "more"], right.Children.Select(node => node.Key));
        Assert.Contains(Flatten(right), node => node.Text == "$1.25");
        Assert.Contains(Flatten(right), node => node.Text == "Vlad");
        var more = Flatten(Assert.Single(right.Children, node => node.Key == "more")).ToArray();
        Assert.DoesNotContain(more, node => node.Kind == UiNodeKind.Button);
        Assert.Equal(new UiChildRef(UIVocabulary.ButtonType, "brain/apps/desktop/settings"), Assert.Single(more, node => node.Key == "more-settings").Reference);
        Assert.Equal(new UiChildRef(UIVocabulary.ButtonType, "brain/apps/desktop/classic"), Assert.Single(more, node => node.Key == "more-classic").Reference);
    }

    [Fact]
    public void TheLayoutSwitchHandsTheShellAReconnectThatChangesTheLayoutFirst()
    {
        var activation = System.Text.Json.JsonDocument.Parse(Desktop.LayoutActivation("brain-1", "classic")).RootElement;
        Assert.Equal(("reconnect", "brain-1", "desktop", "layout", "classic"), (activation.GetProperty("kind").GetString(), activation.GetProperty("brainId").GetString(),
            activation.GetProperty("application").GetString(), activation.GetProperty("operation").GetString(), activation.GetProperty("input").GetString()));
        Assert.Equal("classic", Desktop.ChosenLayout("classic"));
        Assert.Equal("desktop", Desktop.ChosenLayout("desktop"));
        Assert.Null(Desktop.ChosenLayout("v3"));
    }

    [Fact]
    public void ClassicNewDesktopUsesTheDesktopsLayoutActivationShape()
    {
        var workspace = Assert.Single(SolutionProjects.FeaturesOf("os"), app => app.Name == "workspace").Files["Workspace.cs"];
        var names = System.Text.Json.JsonDocument.Parse(Desktop.LayoutActivation("b", "desktop")).RootElement.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Equal(["kind", "brainId", "application", "operation", "input"], names);
        Assert.Contains("new { kind = \"reconnect\", brainId = Scope(brain), application = \"desktop\", operation = \"layout\", input = \"desktop\" }", workspace, StringComparison.Ordinal);
    }

    // Ruling 1: More's Settings hands the shell {kind: settings}, so the shell opens its native screen.
    [Fact]
    public void MoreSettingsProducesTheSettingsActivation()
    {
        var activation = System.Text.Json.JsonDocument.Parse(Desktop.SettingsActivation("brain-1")).RootElement;
        Assert.Equal(["kind", "brainId"], activation.EnumerateObject().Select(property => property.Name));
        Assert.Equal(("settings", "brain-1"), (activation.GetProperty("kind").GetString(), activation.GetProperty("brainId").GetString()));
        var tabbed = System.Text.Json.JsonDocument.Parse(Desktop.SettingsActivation("brain-1", "keys")).RootElement;
        Assert.Equal("keys", tabbed.GetProperty("tab").GetString());
        var desktop = Assert.Single(SolutionProjects.FeaturesOf("os"), app => app.Name == "desktop").Files["Desktop.cs"];
        Assert.Contains("SettingsActivation(Scope(brain))", desktop, StringComparison.Ordinal);
        var dock = Flatten(DesktopLayout.Render(new DesktopModel([], null, [], false, false, null, SettingsButton: "brain/apps/desktop/settings"))).ToArray();
        Assert.Equal(new UiChildRef(UIVocabulary.ButtonType, "brain/apps/desktop/settings"), Assert.Single(dock, node => node.Key == "more-settings").Reference);
    }

    [Fact]
    public void LayoutAndSettingsHaveSmallHandlers()
    {
        var bundle = Assert.Single(SolutionProjects.FeaturesOf("os"), app => app.Name == "desktop");
        Assert.Contains("\"layout\"", bundle.Files["layout-switch.cs"], StringComparison.Ordinal);
        Assert.Contains("\"settings\"", bundle.Files["settings.cs"], StringComparison.Ordinal);
        Assert.Contains("Desktop.SetSettingsButton", bundle.Files["settings.cs"], StringComparison.Ordinal);
        Assert.DoesNotContain("Desktop.Open", bundle.Files["settings.cs"], StringComparison.Ordinal);
        // An old-render `settings` click re-arms the button and re-renders, so the next click carries the activation.
        var settings = bundle.Files["settings.cs"];
        var rearm = settings.IndexOf("await Desktop.SetSettingsButton(brain);", StringComparison.Ordinal);
        Assert.True(rearm >= 0 && settings.IndexOf("await Desktop.Show(brain);", rearm, StringComparison.Ordinal) > rearm);
        Assert.All((string[])["layout-switch.cs", "settings.cs"], file => Assert.InRange(bundle.Files[file].Split('\n').Length, 1, 40));
    }

    [Fact]
    public void WindowsOfferPinOrUnpinButInoStaysPinned()
    {
        var windows = new WindowModel[] { new("ino", "brain/apps/ino", "Ino", "w/ino"), new("notes", "brain/apps/notes", "Notes", "w/notes"), new("calc", "brain/apps/calc", "Calc", "w/calc") };
        var model = new DesktopModel(windows, "calc", [new("ino", "Ino", null, true, true), new("notes", "Notes", null, true, true), new("calc", "Calc", null, false, true)], false, false, null);
        var actions = Flatten(DesktopLayout.Render(model)).Select(node => node.Action).ToArray();
        Assert.Contains("unpin:notes", actions);
        Assert.Contains("pin:calc", actions);
        Assert.DoesNotContain(actions, action => action is "pin:ino" or "unpin:ino");
    }

    [Fact]
    public void PinAndUnpinHaveOneSmallHandler()
    {
        var bundle = Assert.Single(SolutionProjects.FeaturesOf("os"), app => app.Name == "desktop");
        Assert.Contains("\"pin:\"", bundle.Files["dock.cs"], StringComparison.Ordinal);
        Assert.Contains("\"unpin:\"", bundle.Files["dock.cs"], StringComparison.Ordinal);
        Assert.InRange(bundle.Files["dock.cs"].Split('\n').Length, 1, 40);
    }

    [Fact]
    public void DockOrderIsInoFirstThenPinnedThenOpenAppsOnly()
    {
        var titles = new Dictionary<string, string> { ["zeta"] = "Zeta", ["alpha"] = "Alpha", ["ino"] = "Ino", ["notes"] = "Notes", ["beta"] = "Beta", ["gamma"] = "Gamma" };
        // Everything else is reached through the gallery.
        Assert.Equal(["ino", "notes", "zeta", "beta"], Desktop.DockOrder(titles, new HashSet<string> { "zeta", "notes" }, new HashSet<string> { "beta", "notes" }));
    }

    [Fact]
    public void TheGalleryHasBuiltInMineAndFromOthersInOrder()
    {
        var tree = Gallery.Render(Entries, null, null);
        Assert.Equal("gallery-panel", tree.Key);
        Assert.Equal(["Built-in", "Mine", "From others"], tree.Children.Where(node => node.Key.StartsWith("section-", StringComparison.Ordinal)).Select(node => node.Children[0].Text));
        // The heading is set once, as the section's first child.
        Assert.All(tree.Children.Where(node => node.Key.StartsWith("section-", StringComparison.Ordinal)), section => Assert.Null(section.Text));
        Assert.Contains(Flatten(tree), node => node.Kind == UiNodeKind.Button && node.Action == "gallery");
    }

    [Fact]
    public void InstalledEntriesOfferOpenAndForeignOnesOfferGet()
    {
        var actions = Flatten(Gallery.Render(Entries, null, null)).Where(node => node.Kind == UiNodeKind.Button).Select(node => (node.Text, node.Action)).ToArray();
        Assert.Contains(("Open", "open:calculator"), actions);
        Assert.Contains(("Open", "open:notes-2"), actions);
        Assert.Contains(("Get", "get:alice/weather"), actions);
        Assert.DoesNotContain(actions, action => action.Action == "get:shipped/calculator");
    }

    [Fact]
    public void AGetFailureShowsItsNoticeAndStillTheGallery()
    {
        var tree = Gallery.Render(Entries, "You already have an app called weather.", null);
        Assert.Equal("You already have an app called weather.", Assert.Single(Flatten(tree), node => node.Key == "notice").Text);
        Assert.Equal(3, tree.Children.Count(node => node.Key.StartsWith("section-", StringComparison.Ordinal)));
    }

    [Fact]
    public void AnOriginalAndItsCopyAreDistinguishable()
    {
        var cards = Flatten(Gallery.Render(Entries, null, null)).Where(node => node.Kind == UiNodeKind.Card).ToArray();
        Assert.Equal(cards.Length, cards.Select(node => node.Key).Distinct().Count());
        Assert.Contains(cards, node => node.Text == "Notes");
        Assert.Contains(cards, node => node.Text == "Copy of Notes");
    }

    [Fact]
    public void TheDesktopShowsTheGalleryAboveTheDockWhenAsked()
    {
        var model = new DesktopModel([], null, [new("ino", "Ino", null, true, false)], false, true, "Nope", Entries: Entries);
        var tree = DesktopLayout.Render(model);
        Assert.Equal(["gallery-panel", "dock"], tree.Children.Select(node => node.Key));
        // The view refuses duplicate keys: the gallery and the dock's Apps button must not collide.
        var keys = Flatten(tree).Select(node => node.Key).ToArray();
        Assert.Equal(keys.Length, keys.Distinct().Count());
        Assert.Contains(Flatten(tree), node => node.Key == "notice" && node.Text == "Nope");
        Assert.DoesNotContain(DesktopLayout.Render(model with { ShowGallery = false }).Children, node => node.Key == "gallery-panel");
    }

    [Fact]
    public void GalleryAndGetHaveSmallHandlers()
    {
        var bundle = Assert.Single(SolutionProjects.FeaturesOf("os"), app => app.Name == "desktop");
        Assert.Contains("\"gallery\"", bundle.Files["apps.cs"], StringComparison.Ordinal);
        Assert.Contains("\"get:\"", bundle.Files["get.cs"], StringComparison.Ordinal);
        Assert.All((string[])["apps.cs", "get.cs", "open.cs"], file => Assert.InRange(bundle.Files[file].Split('\n').Length, 1, 40));
    }

    [Fact]
    public void CardKeysNeverCollideEvenWhenNamesContainDashes()
    {
        GalleryEntry[] tricky = [new("others", "a-b", "c", "One", "", null, false, false, null), new("others", "a", "b-c", "Two", "", null, false, false, null)];
        var keys = Flatten(Gallery.Render(tricky, null, null)).Select(node => node.Key).ToArray();
        Assert.Equal(keys.Length, keys.Distinct().Count());
    }

    [Fact]
    public void GetNoticesSeparateInstallFromOpenFailures()
    {
        Assert.Equal("", Desktop.GetNotice(new(true, "echo", null), opened: true));
        Assert.Equal("Already have it.", Desktop.GetNotice(new(false, null, "Already have it."), opened: false));
        Assert.Equal("That app could not be installed.", Desktop.GetNotice(new(false, null, null), opened: false));
        Assert.Equal("Installed echo, but it could not be opened.", Desktop.GetNotice(new(true, "echo", null), opened: false));
    }

    [Fact]
    public async Task AGalleryThatCannotLoadStillRendersWithANotice()
    {
        var (entries, notice) = await Desktop.LoadGallery(() => throw new InvalidOperationException("store down"), null);
        Assert.Empty(entries);
        Assert.Equal("The gallery could not be loaded.", notice);
        var (loaded, kept) = await Desktop.LoadGallery(() => Task.FromResult<IReadOnlyList<GalleryEntry>>(Entries), "Earlier");
        Assert.Equal((4, "Earlier"), (loaded.Count, kept));
        await Assert.ThrowsAsync<OperationCanceledException>(() => Desktop.LoadGallery(() => throw new OperationCanceledException(), null));
    }

    [Fact]
    public void ConnectionsOfferAConnectButtonNeuronAndShowConnectedOrNotAvailable()
    {
        var tree = Flatten(Gallery.Render(Connections, null, "brain/apps/desktop/connect")).ToArray();
        var gmail = Assert.Single(tree, node => node.Key == "connect-connection/gmail");
        Assert.Equal((UiNodeKind.Reference, new UiChildRef(UIVocabulary.ButtonType, "brain/apps/desktop/connect/gmail")), (gmail.Kind, gmail.Reference));
        var salesforce = Assert.Single(tree, node => node.Key == "connect-connection/salesforce");
        Assert.Equal((UiNodeKind.Button, "Not available", false), (salesforce.Kind, salesforce.Text, salesforce.Enabled));
        Assert.DoesNotContain(tree, node => node.Key == "connect-connection/github");
        Assert.Contains(tree, node => node.Key == "entry-connection/github" && node.Text == "GitHub" && node.Detail == "Connected.");
        var others = Assert.Single(Gallery.Render(Connections, null, null).Children, node => node.Key == "section-others");
        Assert.Equal(3, Flatten(others).Count(node => node.Kind == UiNodeKind.Card));
    }

    [Fact]
    public void ConnectHandsTheShellItsConnectionWindowForTheConnector()
    {
        var activation = System.Text.Json.JsonDocument.Parse(Desktop.ConnectActivation("brain-1", "gmail")).RootElement;
        Assert.Equal(("connections", "brain-1", "gmail"), (activation.GetProperty("kind").GetString(), activation.GetProperty("brainId").GetString(), activation.GetProperty("connector").GetString()));
        Assert.False(activation.TryGetProperty("url", out _));
    }

    [Fact]
    public void NoScriptStartsALoginAndConnectedHasASmallHandler()
    {
        var bundle = Assert.Single(SolutionProjects.FeaturesOf("os"), app => app.Name == "desktop");
        Assert.DoesNotContain("connect.cs", bundle.Files.Keys);
        Assert.All(bundle.Files.Values, file => Assert.DoesNotContain("/login", file, StringComparison.Ordinal));
        Assert.InRange(bundle.Files["connected.cs"].Split('\n').Length, 1, 40);
    }

    private static readonly GalleryEntry[] Connections =
    [
        new("others", "", "gmail", "Gmail", "Connect your Gmail account.", null, false, false, null, "connection", "gmail"),
        new("others", "", "salesforce", "Salesforce", "Not available on this host.", null, false, false, null, "connection", "salesforce", Available: false),
        new("others", "", "github", "GitHub", "Connected.", null, true, false, null, "connection", "github"),
    ];

    private static readonly GalleryEntry[] Entries =
    [
        new("others", "alice", "weather", "Weather", "Forecasts", null, false, false, null),
        new("builtin", "shipped", "calculator", "Calculator", "Sums", null, true, false, "calculator"),
        new("mine", "me", "notes", "Notes", "Mine", null, true, false, "notes"),
        new("mine", "shipped", "notes-2", "Copy of Notes", "Edited", null, true, false, "notes-2"),
    ];

    [Fact]
    public void AFailedFirstRunNeverSkipsStart()
    {
        var start = Assert.Single(SolutionProjects.FeaturesOf("os"), app => app.Name == "desktop").Files["start.cs"];
        // FirstRun has its own try; Start runs in a separate try after it.
        Assert.Contains("try { await Desktop.FirstRun(brain); }", start, StringComparison.Ordinal);
        Assert.Contains("try { await Desktop.Start(brain); }", start, StringComparison.Ordinal);
    }

    private static DesktopModel Model(int count, string? focused, bool narrow)
    {
        var windows = Enumerable.Range(0, count).Select(i => ((char)('a' + i)).ToString())
            .Select(id => new WindowModel(id, "brain/apps/" + id, "App " + id, "brain/apps/desktop/window/" + id)).ToArray();
        return new(windows, focused, [new("ino", "Ino", null, true, false)], narrow, false, null);
    }

    private static IEnumerable<UiNode> Flatten(UiNode root)
    {
        yield return root;
        foreach (var child in root.Children.SelectMany(Flatten)) { yield return child; }
    }
}
