using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Views;

// Pure composition: durable references retain their public contracts and local UI identity.
public static class InoView
{
    public static UiNode Render(string appKey) => Ui.Column("main",
    [
        Ui.Row("header",
        [
            Ui.Reference("status", new(UIVocabulary.TextType, appKey + "/status")),
            Ui.Button("new", "New conversation", "new"),
        ], extents: [0, 160]),
        Ui.Reference("transcript", new(UIVocabulary.ChatType, appKey + "/transcript")),
        Ui.Reference("composer", new(UIVocabulary.ComposerType, appKey + "/composer")),
    ], extents: [40, 0, 152]);
}
