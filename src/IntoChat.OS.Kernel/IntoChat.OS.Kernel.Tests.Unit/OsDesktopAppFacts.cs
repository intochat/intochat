using IntoChat.OS.Apps;
using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Views;

namespace IntoChat.OS.Kernel.Tests.Unit;

// The desktop declares one startup operation and renders windows above a dock.
public sealed class OsDesktopAppFacts
{
    [Fact]
    public void TheDesktopDeclaresOneStartOperationAndALayoutSetting()
    {
        var bundle = Assert.Single(SolutionProjects.FeaturesOf("os"), app => app.Name == "desktop");
        Assert.Equal(["start"], bundle.Manifest.Operations.Select(operation => operation.Name));
        var layout = Assert.Single(bundle.Manifest.Settings);
        Assert.Equal(("Layout", "desktop"), (layout.Name, layout.Default));
        Assert.Contains("start.cs", bundle.Files.Keys);
        // One startup signal: only start.cs subscribes to `start`.
        Assert.Equal(["start.cs"], bundle.Files.Where(file => file.Value.Contains("\"start\"", StringComparison.Ordinal) && file.Key != "app.cs").Select(file => file.Key));
    }

    [Fact]
    public void AnEmptyDesktopShowsTheDockOnly()
    {
        var model = new DesktopModel([], null, [new("ino", "Ino", null, true, false), new("calculator", "Calculator", null, false, false)], false, false, null);
        var tree = DesktopLayout.Render(model);
        var dock = Assert.Single(tree.Children);
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
        foreach (var (file, prefix) in new[] { ("open.cs", "\"open:\""), ("close.cs", "\"close:\""), ("focus.cs", "\"focus:\""), ("narrow.cs", "\"narrow:\"") })
        {
            Assert.Contains(prefix, bundle.Files[file], StringComparison.Ordinal);
            Assert.InRange(bundle.Files[file].Split('\n').Length, 1, 40);
        }
        Assert.Contains("WorkspaceChanged", bundle.Files["restore.cs"], StringComparison.Ordinal);
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
            false, false, null, Usage: "$1.25", Person: "Vlad");
        var dock = Assert.Single(DesktopLayout.Render(model).Children);
        var buttons = Flatten(dock).Where(node => node.Kind == UiNodeKind.Button).ToArray();
        Assert.Equal(["open:ino", "open:notes", "open:calc"], buttons.Take(3).Select(node => node.Action));
        Assert.Contains(buttons, node => node.Key == "gallery" && node.Text == "Apps");
        var right = Assert.Single(Flatten(dock), node => node.Key == "right");
        Assert.Equal(["usage", "avatar", "more"], right.Children.Select(node => node.Key));
        Assert.Contains(Flatten(right), node => node.Text == "$1.25");
        Assert.Contains(Flatten(right), node => node.Text == "Vlad");
        Assert.Equal(["settings", "layout:classic"], Flatten(Assert.Single(right.Children, node => node.Key == "more"))
            .Where(node => node.Kind == UiNodeKind.Button).Select(node => node.Action));
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
        var tree = Gallery.Render(Entries, null);
        Assert.Equal("gallery-panel", tree.Key);
        Assert.Equal(["Built-in", "Mine", "From others"], tree.Children.Where(node => node.Key.StartsWith("section-", StringComparison.Ordinal)).Select(node => node.Text));
        Assert.Contains(Flatten(tree), node => node.Kind == UiNodeKind.Button && node.Action == "gallery");
    }

    [Fact]
    public void InstalledEntriesOfferOpenAndForeignOnesOfferGet()
    {
        var actions = Flatten(Gallery.Render(Entries, null)).Where(node => node.Kind == UiNodeKind.Button).Select(node => (node.Text, node.Action)).ToArray();
        Assert.Contains(("Open", "open:calculator"), actions);
        Assert.Contains(("Open", "open:notes-2"), actions);
        Assert.Contains(("Get", "get:alice/weather"), actions);
        Assert.DoesNotContain(actions, action => action.Action == "get:shipped/calculator");
    }

    [Fact]
    public void AGetFailureShowsItsNoticeAndStillTheGallery()
    {
        var tree = Gallery.Render(Entries, "You already have an app called weather.");
        Assert.Equal("You already have an app called weather.", Assert.Single(Flatten(tree), node => node.Key == "notice").Text);
        Assert.Equal(3, tree.Children.Count(node => node.Key.StartsWith("section-", StringComparison.Ordinal)));
    }

    [Fact]
    public void AnOriginalAndItsCopyAreDistinguishable()
    {
        var cards = Flatten(Gallery.Render(Entries, null)).Where(node => node.Kind == UiNodeKind.Card).ToArray();
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
