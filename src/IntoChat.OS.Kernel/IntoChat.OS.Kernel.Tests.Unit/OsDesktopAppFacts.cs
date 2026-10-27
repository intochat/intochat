using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Views;

namespace IntoChat.OS.Kernel.Tests.Unit;

// The desktop declares one startup operation and renders windows above a dock.
public sealed class OsDesktopAppFacts
{
    [Fact]
    public void TheDesktopDeclaresStartAndLayoutOperations()
    {
        var bundle = Assert.Single(SolutionProjects.FeaturesOf("os"), app => app.Name == "desktop");
        Assert.Equal(["start", "layout", "open", "narrow"], bundle.Manifest.Operations.Select(operation => operation.Name));
        // The layout is the desktop's own text (apps/desktop/layout), not a setting: a configure would redeploy the desktop.
        Assert.Empty(bundle.Manifest.Settings);
        Assert.Contains("start.cs", bundle.Files.Keys);
        // One startup signal: only start.cs subscribes to `start`.
        Assert.Equal(["start.cs"], bundle.Files.Where(file => file.Value.Contains("\"start\"", StringComparison.Ordinal) && file.Key != "app.cs").Select(file => file.Key));
    }

    [Fact]
    public void TheDesktopIsVersion030()
    {
        var bundle = Assert.Single(SolutionProjects.FeaturesOf("os"), app => app.Name == "desktop");
        Assert.Equal("0.3.0", bundle.Version);
        Assert.Contains("## Scenario: Tabs and favourites", bundle.Spec, StringComparison.Ordinal);
    }

    // Option A: the dock is at the bottom even before any window opens.
    [Fact]
    public void AnEmptyDesktopShowsNoWindowsAndTheDockAtTheBottom()
    {
        var model = new DesktopModel([], null, [new("ino", "Ino", null, true, false), new("calculator", "Calculator", null, false, false)], false);
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
        var chrome = Assert.Single(Flatten(tree), node => node.Key == "chrome-a");
        Assert.Equal(UiNodeKind.Row, chrome.Kind);
        Assert.Contains(Flatten(chrome), node => node.Kind == UiNodeKind.Button && node.Action == "close:a/0");
        Assert.Contains(Flatten(chrome), node => node.Kind == UiNodeKind.Button && node.Action == "focus:a" && node.Text == "App a");
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
            Assert.Equal(["chrome-" + id, "tabs-" + id, "content-" + id], frame.Children.Select(node => node.Key));
            Assert.Equal([40, 38, 0], frame.Extents ?? []);
            Assert.Equal(0, frame.Gap);
        }
        Assert.Equal(78, DesktopLayout.ChromeHeight);
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
        foreach (var (file, prefix) in new[] { ("open.cs", "\"open:\""), ("close.cs", "\"close:\""), ("focus.cs", "\"focus:\""),
            ("tabs.cs", "\"tab:\""), ("tabs.cs", "\"newtab\""), ("favourite.cs", "\"favourite:\""), ("favourite.cs", "\"view:\"") })
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
        IntoChat.OS.Modules.Flutter.Workspace.WorkspaceViewRef[] views = [new("desktop", "brain/apps/desktop", "full", ""), new("workspace", "brain/apps/workspace", "full", ""), new("calculator", "brain/apps/calculator", "full", "")];
        Assert.Equal(["calculator"], Desktop.Windowed("brain", views).Select(view => view.Id));
    }

    [Fact]
    public void TheDockShowsInoFirstThenAppsAndUsageAvatarAndMoreOnTheRight()
    {
        var model = new DesktopModel([], null, [new("ino", "Ino", null, true, false), new("notes", "Notes", null, true, false), new("calc", "Calc", null, false, true)],
            false, Usage: "$1.25", Person: "Vlad", LayoutButton: "brain/apps/desktop/classic", SettingsButton: "brain/apps/desktop/settings");
        var dock = Assert.Single(DesktopLayout.Render(model).Children, node => node.Key == "dock");
        var buttons = Flatten(dock).Where(node => node.Kind == UiNodeKind.Button).ToArray();
        Assert.Equal(["open:ino", "open:notes", "open:calc"], buttons.Take(3).Select(node => node.Action));
        Assert.Contains(buttons, node => node.Key == "apps-open" && node.Text == "Apps" && node.Action == "open:apps" && node.Style == UiStyle.Ghost && node.Icon == "grid");
        var apps = Assert.Single(Flatten(dock), node => node.Key == "apps");
        Assert.Equal(["dock-ino", "dock-notes", "dock-calc", "newtab", "apps-open"], apps.Children.Select(node => node.Key));
        Assert.Equal(("New tab", "newtab", "plus"), (apps.Children[3].Text, apps.Children[3].Action, apps.Children[3].Icon));
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

    // More's Settings hands the shell a workspace activation: open the Settings app's window and ask it for the tab.
    [Fact]
    public void SettingsActivationOpensTheSettingsAppOnATab()
    {
        static (string, string, string, string, string) Read(string json)
        {
            var root = System.Text.Json.JsonDocument.Parse(json).RootElement;
            return (root.GetProperty("kind").GetString()!, root.GetProperty("brainId").GetString()!, root.GetProperty("application").GetString()!,
                root.GetProperty("operation").GetString()!, root.GetProperty("input").GetString()!);
        }
        Assert.Equal(("workspace", "brain-1", "settings", "open", "general"), Read(Desktop.SettingsActivation("brain-1")));
        Assert.Equal(("workspace", "brain-1", "settings", "open", "keys"), Read(Desktop.SettingsActivation("brain-1", "keys")));
        Assert.Equal(["kind", "brainId", "application", "operation", "input"],
            System.Text.Json.JsonDocument.Parse(Desktop.SettingsActivation("b")).RootElement.EnumerateObject().Select(property => property.Name));
        var desktop = Assert.Single(SolutionProjects.FeaturesOf("os"), app => app.Name == "desktop").Files["Desktop.cs"];
        Assert.Contains("SettingsActivation(Scope(brain))", desktop, StringComparison.Ordinal);
        var dock = Flatten(DesktopLayout.Render(new DesktopModel([], null, [], false, SettingsButton: "brain/apps/desktop/settings"))).ToArray();
        Assert.Equal(new UiChildRef(UIVocabulary.ButtonType, "brain/apps/desktop/settings"), Assert.Single(dock, node => node.Key == "more-settings").Reference);
        // Ino's setup card arms the same shape for the API keys tab.
        Assert.Equal(Desktop.SettingsActivation("b1", "keys"), InoModels.KeysActivation("b1"));
    }

    [Fact]
    public void TheDockAppsButtonOpensTheAppsApp()
    {
        foreach (var narrow in new[] { false, true })
        {
            var nodes = Flatten(DesktopLayout.Render(new DesktopModel([], null, [new("ino", "Ino", null, true, false)], narrow))).ToArray();
            var apps = Assert.Single(nodes, node => node.Key == "apps-open");
            Assert.Equal((UiNodeKind.Button, "Apps", "open:apps", "grid"), (apps.Kind, apps.Text, apps.Action, apps.Icon));
            Assert.DoesNotContain(nodes, node => node.Action == "gallery");
        }
        var bundle = Assert.Single(SolutionProjects.FeaturesOf("os"), app => app.Name == "desktop");
        Assert.Contains("## Scenario: Apps opens the Apps window", bundle.Spec, StringComparison.Ordinal);
        Assert.Contains("## Scenario: Settings opens the Settings window on a tab", bundle.Spec, StringComparison.Ordinal);
    }

    [Fact]
    public void NoGalleryRemainsInTheDesktopTree()
    {
        var bundle = Assert.Single(SolutionProjects.FeaturesOf("os"), app => app.Name == "desktop");
        Assert.DoesNotContain("Gallery.cs", bundle.Files.Keys);
        Assert.DoesNotContain("apps.cs", bundle.Files.Keys);
        Assert.DoesNotContain("get.cs", bundle.Files.Keys);
        Assert.All(bundle.Files.Values, file => Assert.DoesNotContain("\"get:", file, StringComparison.Ordinal));
        Assert.All(bundle.Files.Values, file => Assert.DoesNotContain("ShowGallery", file, StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(DesktopModel).GetProperties(), property => property.Name is "ShowGallery" or "Notice" or "Entries" or "ConnectButtons");
        var keys = Flatten(DesktopLayout.Render(Model(3, "b", narrow: false))).Select(node => node.Key).ToArray();
        Assert.DoesNotContain(keys, key => key.StartsWith("gallery", StringComparison.Ordinal) || key.StartsWith("entry-", StringComparison.Ordinal));
        Assert.Equal(keys.Length, keys.Distinct().Count());
    }

    // The desktop root's activation receiver is the Workspace app (OsWorkspaceRoot.ActivationApp): an
    // {application, operation, input} activation opens or focuses that app's window, then hands it the request.
    [Fact]
    public void OpeningWithInputForwardsItToTheApp()
    {
        var root = File.ReadAllText(Path.Combine(SolutionProjects.DirectoryOf("IntoChat.OS.Kernel"), "OsWorkspaceRoot.cs"));
        Assert.Contains("public string ActivationApp => \"workspace\";", root, StringComparison.Ordinal);
        var workspace = Assert.Single(SolutionProjects.FeaturesOf("os"), app => app.Name == "workspace").Files["Workspace.cs"];
        var activate = workspace[workspace.IndexOf("public static async Task Activate(", StringComparison.Ordinal)..];
        activate = activate[..activate.IndexOf("public static async Task Navigate(", StringComparison.Ordinal)];
        var open = activate.IndexOf("Encode(state.Revision, \"open\", name)", StringComparison.Ordinal);
        var request = activate.IndexOf(".Request(action.Operation, action.Input ?? \"\")", StringComparison.Ordinal);
        Assert.True(open >= 0 && request > open);
        // Settings and Apps are visible apps, so the window they open shows on the desktop.
        Assert.Equal(["settings", "apps"], Desktop.Windowed("brain", [new("settings", "brain/apps/settings", "full", ""), new("apps", "brain/apps/apps", "full", "")]).Select(view => view.Id));
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
        var windows = new[] { Window("w-1", "ino"), Window("w-2", "notes", "ino", "notes"), Window("w-3", "calc") };
        var model = new DesktopModel(windows, "w-3", [new("ino", "Ino", null, true, true), new("notes", "Notes", null, true, true), new("calc", "Calc", null, false, true)], false);
        var nodes = Flatten(DesktopLayout.Render(model)).ToArray();
        Assert.Equal(("unpin:notes", "pin-off"), (Assert.Single(nodes, node => node.Key == "unpin-w-2").Action, nodes.Single(node => node.Key == "unpin-w-2").Icon));
        Assert.Equal(("pin:calc", "pin"), (Assert.Single(nodes, node => node.Key == "pin-w-3").Action, nodes.Single(node => node.Key == "pin-w-3").Icon));
        Assert.DoesNotContain(nodes, node => node.Key is "pin-w-1" or "unpin-w-1");
        Assert.DoesNotContain(nodes, node => node.Action is "pin:ino" or "unpin:ino");
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
        // Everything else is reached through the Apps app.
        Assert.Equal(["ino", "notes", "zeta", "beta"], Desktop.DockOrder(titles, new HashSet<string> { "zeta", "notes" }, new HashSet<string> { "beta", "notes" }));
    }

    [Fact]
    public void ConnectHandsTheShellItsConnectionWindowForTheConnector()
    {
        var activation = System.Text.Json.JsonDocument.Parse(Desktop.ConnectActivation("brain-1", "gmail")).RootElement;
        Assert.Equal(("connections", "brain-1", "gmail"), (activation.GetProperty("kind").GetString(), activation.GetProperty("brainId").GetString(), activation.GetProperty("connector").GetString()));
        Assert.False(activation.TryGetProperty("url", out _));
    }

    [Fact]
    public void NoScriptStartsALoginAndNoConnectionCardsRemain()
    {
        var bundle = Assert.Single(SolutionProjects.FeaturesOf("os"), app => app.Name == "desktop");
        Assert.DoesNotContain("connect.cs", bundle.Files.Keys);
        Assert.All(bundle.Files.Values, file => Assert.DoesNotContain("/login", file, StringComparison.Ordinal));
        Assert.DoesNotContain("connected.cs", bundle.Files.Keys);
    }

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
            .Select(id => new WindowModel(id, "App " + id, "brain/apps/desktop/window/" + id, [new(id, id, "App " + id, false)], id, id)).ToArray();
        return new(windows, focused, [new("ino", "Ino", null, true, false)], narrow);
    }

    [Fact]
    public void EachWindowShowsItsTabStripWithTheActiveTabPrimary()
    {
        var model = new DesktopModel([Window("w-1", "notes", "ino", "notes", "calc")], "w-1", [], false);
        var strip = Assert.Single(Flatten(DesktopLayout.Render(model)), node => node.Key == "tabs-w-1");
        Assert.Equal(UiNodeKind.Tabs, strip.Kind);
        Assert.Equal(["tab-w-1-0", "tab-w-1-1", "tab-w-1-2"], strip.Children.Select(node => node.Key));
        Assert.Equal(["tab:w-1/0", "tab:w-1/1", "tab:w-1/2"], strip.Children.Select(node => node.Action));
        Assert.Equal(["Ino", "Notes", "Calc"], strip.Children.Select(node => node.Text));
        Assert.Equal([UiStyle.Ghost, UiStyle.Primary, UiStyle.Ghost], strip.Children.Select(node => node.Style));
    }

    [Fact]
    public void ChromeUsesIconButtonsForNewTabStarPinAndClose()
    {
        var model = new DesktopModel([Window("w-1", "calc", "ino", "calc")], "w-1", [new("calc", "Calc", null, false, true)], false);
        var chrome = Assert.Single(Flatten(DesktopLayout.Render(model)), node => node.Key == "chrome-w-1");
        Assert.Equal(["focus-w-1", "newtab-w-1", "star-w-1", "pin-w-1", "close-w-1"], chrome.Children.Select(node => node.Key));
        Assert.Equal([0, 32, 32, 32, 32], chrome.Extents ?? []);
        Assert.Equal(4, chrome.Gap);
        Assert.All(chrome.Children, node => Assert.Equal(UiStyle.Ghost, node.Style));
        Assert.Equal(["plus", "star", "pin", "x"], chrome.Children.Skip(1).Select(node => node.Icon));
        Assert.Equal(["newtab:w-1", "favourite:w-1", "pin:calc", "close:w-1/1"], chrome.Children.Skip(1).Select(node => node.Action));
        Assert.Equal(("Calc", "focus:w-1"), (chrome.Children[0].Text, chrome.Children[0].Action));
        Assert.Equal("Add to favourites", chrome.Children[2].Text);
        var starred = model with { Windows = [Window("w-1", "calc") with { Tabs = [new("calc", "calc", "Calc", true)] }] };
        Assert.Equal("Remove from favourites", Assert.Single(Flatten(DesktopLayout.Render(starred)), node => node.Key == "star-w-1").Text);
        var ino = model with { Windows = [Window("w-1", "ino")] };
        Assert.Equal([0, 32, 32, 32], Assert.Single(Flatten(DesktopLayout.Render(ino)), node => node.Key == "chrome-w-1").Extents ?? []);
    }

    [Fact]
    public void TheInoDockEntryIsPrimaryAndOthersAreGhost()
    {
        var model = new DesktopModel([], null, [new("ino", "Ino", null, true, false), new("notes", "Notes", null, true, false)], false);
        var nodes = Flatten(DesktopLayout.Render(model)).ToArray();
        var ino = Assert.Single(nodes, node => node.Key == "dock-ino");
        Assert.Equal((UiStyle.Primary, "ino", "Ino", "open:ino"), (ino.Style, ino.Icon, ino.Text, ino.Action));
        Assert.Equal(UiStyle.Ghost, Assert.Single(nodes, node => node.Key == "dock-notes").Style);
        Assert.Equal(UiStyle.Ghost, Assert.Single(nodes, node => node.Key == "newtab").Style);
    }

    [Fact]
    public void FavouritesFollowInoAndAreHiddenWhenEmpty()
    {
        var model = new DesktopModel([], null, [new("ino", "Ino", null, true, false), new("notes", "Notes", null, true, false)], false);
        Assert.DoesNotContain(Flatten(DesktopLayout.Render(model)), node => node.Key == "favourites");
        Assert.DoesNotContain(Flatten(DesktopLayout.Render(model with { Favourites = [] })), node => node.Key == "favourites");
        var with = model with { Favourites = [new("calc", "Calc", "open:calc", false), new("v-notes", "Notes list", "view:v-notes", true)] };
        var apps = Assert.Single(Flatten(DesktopLayout.Render(with)), node => node.Key == "apps");
        var favourites = apps.Children[1];
        Assert.Equal(("favourites", UiNodeKind.Row, 4.0), (favourites.Key, favourites.Kind, favourites.Gap));
        Assert.Equal(["fav-0", "fav-1"], favourites.Children.Select(node => node.Key));
        Assert.Equal(["open:calc", "view:v-notes"], favourites.Children.Select(node => node.Action));
        Assert.Equal(["grid", "list"], favourites.Children.Select(node => node.Icon));
        Assert.All(favourites.Children, node => Assert.Equal(UiStyle.Secondary, node.Style));
    }

    [Fact]
    public void NarrowShowsTheFocusedWindowWithTabsAndFavouritesInAMenu()
    {
        var model = new DesktopModel([Window("w-1", "ino"), Window("w-2", "notes", "notes", "calc")], "w-2", [new("ino", "Ino", null, true, true)], true,
            Favourites: [new("calc", "Calc", "open:calc", false)]);
        var nodes = Flatten(DesktopLayout.Render(model)).ToArray();
        Assert.DoesNotContain(nodes, node => node.Key == "frame-w-1");
        Assert.Contains(nodes, node => node.Key == "tabs-w-2" && node.Children.Length == 2);
        var menu = Assert.Single(nodes, node => node.Key == "favourites");
        Assert.Equal((UiNodeKind.Menu, "Favourites", "star"), (menu.Kind, menu.Text, menu.Icon));
        Assert.Equal(["open:calc"], menu.Children.Select(node => node.Action));
    }

    [Fact]
    public void ARefusalShowsAToastAboveTheDock()
    {
        var model = new DesktopModel([Window("w-1", "ino")], "w-1", [new("ino", "Ino", null, true, true)], false, Toast: WindowOps.WindowsRefusal);
        var tree = DesktopLayout.Render(model);
        Assert.Equal(["windows", "toast", "dock"], tree.Children.Select(node => node.Key));
        Assert.Equal([0, 48, 68], tree.Extents ?? []);
        var toast = tree.Children[1];
        Assert.Equal((UiNodeKind.Toast, "You can have at most 8 windows. Close one to open another.", UiTone.Error), (toast.Kind, toast.Text, toast.Tone));
        Assert.Equal(["windows", "dock"], DesktopLayout.Render(model with { Toast = null }).Children.Select(node => node.Key));
    }

    [Fact]
    public void TabAndWindowKeysNeverCollide()
    {
        var model = new DesktopModel([Window("w-a", "a-b", "a-b", "b"), Window("w-a-b", "c", "c", "a-b-c")], "w-a",
            [new("ino", "Ino", null, true, false), new("a-b", "AB", null, false, true)], false, Favourites: [new("a-b", "AB", "open:a-b", false)]);
        var keys = Flatten(DesktopLayout.Render(model)).Select(node => node.Key).ToArray();
        Assert.Equal(keys.Length, keys.Distinct().Count());
    }

    [Fact]
    public void NewTabUsesInoWhenThePreferenceNamesAHiddenApp()
    {
        var installed = new HashSet<string> { "ino", "notes", "desktop" };
        Assert.Equal("notes", Desktop.NewTabChoice("notes", installed));
        Assert.Equal("ino", Desktop.NewTabChoice("desktop", installed));
        Assert.Equal("ino", Desktop.NewTabChoice("", installed));
        Assert.Equal("ino", Desktop.NewTabChoice(null, installed));
        Assert.Equal("ino", Desktop.NewTabChoice("weather", installed));
    }

    // Rows share space equally unless given extents: the dock sizes every item from its label, so items pack left.
    [Fact]
    public void TheDockPacksItemsLeftInLabelSizedSlots()
    {
        var model = new DesktopModel([], null, [new("ino", "Ino", null, true, false), new("notes", "Notes", null, true, false)], false,
            Favourites: [new("calc", "Calc", "open:calc", false), new("v-notes", "Notes list", "view:v-notes", true), new("files", "Files", "open:files", false)]);
        var apps = Assert.Single(Flatten(DesktopLayout.Render(model)), node => node.Key == "apps");
        Assert.Equal(["dock-ino", "favourites", "dock-notes", "newtab", "apps-open"], apps.Children.Select(node => node.Key));
        var extents = apps.Extents ?? [];
        Assert.Equal(apps.Children.Length, extents.Length);
        Assert.All(extents, extent => Assert.True(extent > 0));
        var favourites = apps.Children[1];
        var widths = favourites.Extents ?? [];
        Assert.Equal(favourites.Children.Length, widths.Length);
        Assert.Equal(widths.Sum() + 4 * (favourites.Children.Length - 1), extents[1]);
        // A longer label gets a wider slot.
        Assert.True(widths[1] > widths[0]);
    }

    // Figma's favourites show their names: labelled buttons with an icon, not icon-only ghosts.
    [Fact]
    public void FavouritesAreLabelledButtons()
    {
        var model = new DesktopModel([], null, [new("ino", "Ino", null, true, false)], false, Favourites: [new("calc", "Calc", "open:calc", false)]);
        var favourite = Assert.Single(Flatten(DesktopLayout.Render(model)), node => node.Key == "fav-0");
        Assert.Equal((UiStyle.Secondary, "grid", "Calc"), (favourite.Style, favourite.Icon, favourite.Text));
    }

    // At 390 px a fixed 360 px right group leaves no room: narrow folds it into one More menu, keeping
    // Settings and Classic layout as button neurons so their activations still reach the shell.
    [Fact]
    public void NarrowFoldsTheRightGroupIntoAMoreMenu()
    {
        var model = new DesktopModel([], null, [new("ino", "Ino", null, true, false)], true, Usage: "$1.25", Person: "Vlad",
            LayoutButton: "brain/apps/desktop/classic", SettingsButton: "brain/apps/desktop/settings");
        var dock = Assert.Single(DesktopLayout.Render(model).Children, node => node.Key == "dock");
        Assert.Equal(["apps", "more"], dock.Children.Select(node => node.Key));
        Assert.Equal([0, 40], dock.Extents ?? []);
        var more = dock.Children[1];
        Assert.Equal((UiNodeKind.Menu, "More", "more-horizontal"), (more.Kind, more.Text, more.Icon));
        Assert.Equal(["usage", "avatar", "more-settings", "more-classic"], more.Children.Select(node => node.Key));
        Assert.Equal(new UiChildRef(UIVocabulary.ButtonType, "brain/apps/desktop/settings"), more.Children[2].Reference);
        Assert.Equal(new UiChildRef(UIVocabulary.ButtonType, "brain/apps/desktop/classic"), more.Children[3].Reference);
        Assert.Equal([0, 360], Assert.Single(DesktopLayout.Render(model with { Narrow = false }).Children, node => node.Key == "dock").Extents ?? []);
    }

    // The toast belongs to the workspace revision it refused at: any later change, from anywhere, hides it.
    [Fact]
    public void ARefusalToastShowsOnlyAtItsRevision()
    {
        var stored = Desktop.ToastAt(7, WindowOps.TabsRefusal);
        Assert.Equal(WindowOps.TabsRefusal, Desktop.ShownToast(stored, 7));
        Assert.Null(Desktop.ShownToast(stored, 8));
        Assert.Null(Desktop.ShownToast("", 7));
        Assert.Null(Desktop.ShownToast("not a toast", 7));
        var change = Assert.Single(SolutionProjects.FeaturesOf("os"), app => app.Name == "desktop").Files["Desktop.cs"];
        var apply = change.IndexOf("await State(brain).Apply(update", StringComparison.Ordinal);
        Assert.True(apply >= 0 && change.IndexOf("await Toast(brain).Set(\"\")", apply, StringComparison.Ordinal) > apply);
    }

    [Fact]
    public void TabIndicesResolveAgainstVisibleTabsAndStaleOnesAreNull()
    {
        static IntoChat.OS.Modules.Flutter.Workspace.WorkspaceViewRef View(string id) => new(id, "brain/apps/" + id, "full", "");
        var state = new IntoChat.OS.Modules.Flutter.Workspace.WorkspaceState(1, [], [])
        { Windows = [new("w-1", [View("notes"), View("desktop"), View("calc")], "calc")] };
        Assert.Equal("notes", Desktop.TabAt("brain", state, "w-1", "0"));
        // The hidden desktop tab is skipped, so index 1 is calc.
        Assert.Equal("calc", Desktop.TabAt("brain", state, "w-1", "1"));
        Assert.Null(Desktop.TabAt("brain", state, "w-1", "2"));
        Assert.Null(Desktop.TabAt("brain", state, "w-9", "0"));
        Assert.Null(Desktop.TabAt("brain", state, "w-1", "-1"));
        Assert.Null(Desktop.TabAt("brain", state, "w-1", "x"));
        Assert.Null(Desktop.TabAt("brain", state, "w-1", ""));
        Assert.Equal(("w-1", "3"), Desktop.WindowAndIndex("w-1/3"));
        Assert.Equal(("a/b", "3"), Desktop.WindowAndIndex("a/b/3"));
        Assert.Equal(("w-1", ""), Desktop.WindowAndIndex("w-1"));
    }

    [Fact]
    public void FocusOnAHiddenActiveTabStillResolvesToItsWindow()
    {
        static IntoChat.OS.Modules.Flutter.Workspace.WorkspaceViewRef View(string id) => new(id, "brain/apps/" + id, "full", "");
        var state = new IntoChat.OS.Modules.Flutter.Workspace.WorkspaceState(1, [], [])
        {
            Windows = [new("w-1", [View("ino")], "ino"), new("w-2", [View("notes"), View("desktop")], "desktop")],
            FocusedViewId = "desktop",
        };
        Assert.Equal("w-2", Desktop.FocusedWindow("brain", state));
        Assert.Null(Desktop.FocusedWindow("brain", state with { FocusedViewId = "gone" }));
    }

    // A window whose tabs are the given apps (just the active one when none are given).
    private static WindowModel Window(string id, string active, params string[] apps)
    {
        string[] names = apps.Length == 0 ? [active] : apps;
        TabModel[] tabs = [.. names.Select(name => new TabModel(name, name, char.ToUpperInvariant(name[0]) + name[1..], false))];
        return new(id, tabs.First(tab => tab.App == active).Title, "brain/apps/desktop/window/" + id, tabs, active, active);
    }

    private static IEnumerable<UiNode> Flatten(UiNode root)
    {
        yield return root;
        foreach (var child in root.Children.SelectMany(Flatten)) { yield return child; }
    }
}
