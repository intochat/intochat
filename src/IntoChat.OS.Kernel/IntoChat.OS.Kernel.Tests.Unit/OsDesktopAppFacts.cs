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
