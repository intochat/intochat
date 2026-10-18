using IntoChat.OS.Flutter.Views;

namespace IntoChat.OS.Kernel.Tests.Unit;

public sealed class ImmutableAppViewFacts
{
    [Fact]
    public void CalculatorTreeContainsTheCompleteKeypadAndCurrentExpression()
    {
        var tree = Keypad.Render("14");
        var nodes = Flatten(tree).ToArray();
        Assert.Equal("14", Assert.Single(nodes, node => node.Key == "display").Text);
        var buttons = nodes.Where(node => node.Kind == UiNodeKind.Button).ToArray();
        Assert.Equal(Keypad.Keys, buttons.Select(node => node.Action));
        Assert.Equal(Keypad.Labels, buttons.Select(node => node.Text));
        Assert.Equal(nodes.Length, nodes.Select(node => node.Key).Distinct().Count());
        Assert.DoesNotContain(nodes, node => node.Kind == UiNodeKind.Reference);
    }

    [Fact]
    public void HistoryTreeIncludesTheNewestTenRunsWithoutRemoteWidgetReferences()
    {
        var runs = Enumerable.Range(1, 12).Select(index => new BrainView.Run(
            "brain/apps/example/run-" + index, "run", "Succeeded", index * 1000, "trace", "m" + index));
        var nodes = Flatten(BrainView.Render(runs)).ToArray();
        Assert.Contains(nodes, node => node.Kind == UiNodeKind.Card && node.Text == "Recent behavior runs");
        var text = Assert.Single(nodes, node => node.Kind == UiNodeKind.Text).Text;
        Assert.NotNull(text);
        Assert.Equal(10, text.Split('\n').Length);
        Assert.StartsWith("- brain/apps/example/run-12", text);
        Assert.EndsWith("run-3 · Succeeded · 3 ms · trace", text);
        Assert.DoesNotContain(nodes, node => node.Kind == UiNodeKind.Reference);
    }

    private static IEnumerable<UiNode> Flatten(UiNode root)
    {
        yield return root;
        foreach (var child in root.Children.SelectMany(Flatten)) { yield return child; }
    }
}
