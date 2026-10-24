using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Views;

namespace IntoChat.OS.Kernel.Tests.Unit;

public sealed class AssistantViewFacts
{
    [Fact]
    public void RenderingAnotherAppKeepsReferencesInItsScopeAndAllocatesFreshTrees()
    {
        var empty = new InoViewModel([], "main", "");
        var first = InoView.Render("one/apps/ino", empty);
        var second = InoView.Render("two/apps/ino", empty);
        Assert.Equal(new UiChildRef(UIVocabulary.ChatType, "two/apps/ino/transcript"), second.Children[1].Children[1].Reference);
        Assert.Equal(new UiChildRef(UIVocabulary.ComposerType, "two/apps/ino/composer"), second.Children[1].Children[2].Reference);
        Assert.Equal(new UiChildRef(UIVocabulary.TextFieldType, "two/apps/ino/chat-search"), second.Children[0].Children[1].Reference);
        first.Children[0] = Ui.Text("changed", "changed");
        Assert.Equal("sidebar", second.Children[0].Key);
        Assert.Equal("sidebar", InoView.Render("one/apps/ino", empty).Children[0].Key);
    }
}
