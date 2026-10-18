using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Views;

namespace IntoChat.OS.Kernel.Tests.Unit;

public sealed class AssistantViewFacts
{
    [Fact]
    public void TranscriptFillsBetweenHeaderAndDurableComposer()
    {
        const string app = "brain/apps/ino";
        var root = InoView.Render(app);
        Assert.Equal(UiNodeKind.Column, root.Kind);
        Assert.Equal(new double[] { 40, 0, 152 }, root.Extents);
        Assert.Equal(["header", "transcript", "composer"], root.Children.Select(node => node.Key));
        Assert.Equal(new UiChildRef(UIVocabulary.ChatType, app + "/transcript"), root.Children[1].Reference);
        Assert.Equal(new UiChildRef(UIVocabulary.ComposerType, app + "/composer"), root.Children[2].Reference);
        var header = root.Children[0];
        Assert.Equal(UiNodeKind.Row, header.Kind);
        Assert.Equal(new double[] { 0, 160 }, header.Extents);
        Assert.Equal(new UiChildRef(UIVocabulary.TextType, app + "/status"), header.Children[0].Reference);
        var button = header.Children[1];
        Assert.Equal(UiNodeKind.Button, button.Kind);
        Assert.Equal("new", button.Key);
        Assert.Equal("new", button.Action);
        Assert.Equal("New conversation", button.Text);
        Assert.True(button.Enabled);
        Assert.Null(button.Reference);
    }

    [Fact]
    public void RenderingAnotherAppKeepsReferencesInItsScopeAndAllocatesFreshTrees()
    {
        var first = InoView.Render("one/apps/ino");
        var second = InoView.Render("two/apps/ino");
        Assert.Equal(new UiChildRef(UIVocabulary.ChatType, "two/apps/ino/transcript"), second.Children[1].Reference);
        Assert.Equal(new UiChildRef(UIVocabulary.ComposerType, "two/apps/ino/composer"), second.Children[2].Reference);
        first.Children[0] = Ui.Text("changed", "changed");
        Assert.Equal("header", second.Children[0].Key);
        Assert.Equal("header", InoView.Render("one/apps/ino").Children[0].Key);
    }
}
