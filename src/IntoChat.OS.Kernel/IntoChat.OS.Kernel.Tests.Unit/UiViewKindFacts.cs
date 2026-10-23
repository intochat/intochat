using IntoChat.OS.Flutter.Views;

namespace IntoChat.OS.Kernel.Tests.Unit;

public sealed class UiViewKindFacts
{
    [Fact]
    public void KitKindsStylesAndTonesKeepTheirWireNumbers()
    {
        Assert.Equal(Enumerable.Range(0, 13), Enum.GetValues<UiNodeKind>().Select(kind => (int)kind));
        Assert.Equal([UiNodeKind.Menu, UiNodeKind.Modal, UiNodeKind.Toast, UiNodeKind.List, UiNodeKind.Badge, UiNodeKind.Avatar, UiNodeKind.Divider], Enum.GetValues<UiNodeKind>()[6..]);
        Assert.Equal(["Primary", "Secondary", "Ghost", "Danger", "Link"], Enum.GetNames<UiStyle>());
        Assert.Equal(["Neutral", "Done", "Working", "Error"], Enum.GetNames<UiTone>());
    }

    [Fact]
    public void ExistingButtonCallsDefaultToSecondaryNeutralWithoutIcon()
    {
        var button = Ui.Button("go", "Go", "navigate", false);
        Assert.Equal((UiNodeKind.Button, UiStyle.Secondary, UiTone.Neutral, null, false), (button.Kind, button.Style, button.Tone, button.Icon, button.Enabled));
    }

    [Fact]
    public void BuildersProduceTheirKinds()
    {
        var icon = Ui.IconButton("close", "x", "Close", "close");
        Assert.Equal((UiNodeKind.Button, UiStyle.Ghost, "x", "Close", "close"), (icon.Kind, icon.Style, icon.Icon, icon.Text, icon.Action));
        var menu = Ui.Menu("menu", "More", [icon], "ellipsis");
        Assert.Equal((UiNodeKind.Menu, "More", "ellipsis", null), (menu.Kind, menu.Text, menu.Icon, menu.Action));
        var modal = Ui.Modal("modal", "Title", "Detail", [icon]);
        Assert.Equal((UiNodeKind.Modal, "Title", "Detail", 1), (modal.Kind, modal.Text, modal.Detail, modal.Children.Length));
        Assert.Empty(Ui.Toast("toast", "Saved").Children);
        Assert.Equal((UiNodeKind.Toast, UiTone.Error, "close"), (Ui.Toast("toast", "Failed", UiTone.Error, icon).Kind, Ui.Toast("toast", "Failed", UiTone.Error, icon).Tone, Ui.Toast("toast", "Failed", UiTone.Error, icon).Children[0].Key));
        Assert.Equal(UiNodeKind.List, Ui.List("list", []).Kind);
        Assert.Equal((UiNodeKind.Badge, UiTone.Working), (Ui.Badge("badge", "Busy", UiTone.Working).Kind, Ui.Badge("badge", "Busy", UiTone.Working).Tone));
        Assert.Equal((UiNodeKind.Avatar, "VH"), (Ui.Avatar("avatar", "VH").Kind, Ui.Avatar("avatar", "VH").Text));
        Assert.Equal((UiNodeKind.Divider, null), (Ui.Divider("divider").Kind, Ui.Divider("divider").Text));
    }
}
