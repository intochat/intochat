using IntoChat.OS.Apps;
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
        var model = new DesktopModel([new("calculator", "brain/apps/calculator", "Calculator", "brain/apps/desktop/window-calculator")], "calculator",
            [new("ino", "Ino", null, true, false)], false, false, null);
        var tree = DesktopLayout.Render(model);
        Assert.Equal(["windows", "dock"], tree.Children.Select(node => node.Key));
        var window = Assert.Single(Flatten(tree), node => node.Kind == UiNodeKind.Reference);
        Assert.Equal(new IntoChat.OS.Flutter.UiChildRef("viewport", "brain/apps/desktop/window-calculator"), window.Reference);
    }

    private static IEnumerable<UiNode> Flatten(UiNode root)
    {
        yield return root;
        foreach (var child in root.Children.SelectMany(Flatten)) { yield return child; }
    }
}
