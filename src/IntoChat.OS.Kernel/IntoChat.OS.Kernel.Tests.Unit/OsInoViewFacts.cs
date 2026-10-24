using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Chat;
using IntoChat.OS.Flutter.Views;
using Xunit;

namespace IntoChat.OS.Kernel.Tests.Unit;

public sealed class OsInoViewFacts
{
    private const string App = "brain/apps/ino-data";
    private static readonly DateTimeOffset Then = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);
    private static readonly string Older = Id(1), Newer = Id(2), Newest = Id(3);

    private static string Id(int n) => "c-" + new string((char)('0' + n), 32);

    private static ChatIndexEntry Entry(string id, string title, int minutes, bool archived = false) => new(id, title, Then.AddMinutes(minutes), archived);

    private static IReadOnlyList<ChatIndexEntry> Three() =>
        [Entry(Older, "Older", 1), Entry(Newest, "Newest", 3), Entry(Newer, "Newer", 2)];

    private static IEnumerable<UiNode> Flatten(UiNode node) => node.Children.SelectMany(Flatten).Prepend(node);

    private static UiNode Find(UiNode root, string key) => Flatten(root).Single(node => node.Key == key);

    [Fact]
    public void TheSidebarListsChatsNewestFirstWithTheSelectedOneSecondary()
    {
        var root = InoView.Render(App, new(Three(), Newer, ""));
        Assert.Equal(UiNodeKind.Row, root.Kind);
        Assert.Equal(["sidebar", "main"], root.Children.Select(node => node.Key));
        Assert.Equal(new[] { InoLayout.SidebarWidth, 0 }, root.Extents);
        Assert.Equal(0, root.Gap);
        var sidebar = root.Children[0];
        Assert.Equal(new double[] { 40, InoLayout.SearchHeight, 0 }, sidebar.Extents);
        var add = sidebar.Children[0];
        Assert.Equal((UiStyle.Primary, "plus", "New chat", "new"), (add.Style, add.Icon!, add.Text!, add.Action!));
        Assert.Equal(new UiChildRef(UIVocabulary.TextFieldType, App + "/chat-search"), sidebar.Children[1].Reference);
        var rows = Find(root, "chats");
        Assert.Equal(UiNodeKind.List, rows.Kind);
        var opens = rows.Children.Select(row => row.Children[0]).ToArray();
        Assert.Equal(["Newest", "Newer", "Older"], opens.Select(open => open.Text));
        Assert.Equal(["chat:" + Newest, "chat:" + Newer, "chat:" + Older], opens.Select(open => open.Action));
        Assert.Equal([UiStyle.Ghost, UiStyle.Secondary, UiStyle.Ghost], opens.Select(open => open.Style));
        var main = root.Children[1];
        Assert.Equal(new[] { InoLayout.HeaderHeight, 0, InoLayout.ComposerHeight }, main.Extents);
        Assert.Equal(["header", "transcript", "composer"], main.Children.Select(node => node.Key));
        Assert.Equal(new UiChildRef(UIVocabulary.TextType, App + "/status"), main.Children[0].Children[0].Reference);
        Assert.Equal(new UiChildRef(UIVocabulary.ChatType, App + "/transcript"), main.Children[1].Reference);
        Assert.Equal(new UiChildRef(UIVocabulary.ComposerType, App + "/composer"), main.Children[2].Reference);
    }

    [Fact]
    public void EachRowHasAMenuWithRenameAndArchive()
    {
        var row = Find(InoView.Render(App, new(Three(), Newer, "")), "chat-0");
        Assert.Equal(new double[] { 0, 32 }, row.Extents);
        var menu = row.Children[1];
        Assert.Equal((UiNodeKind.Menu, "Chat actions", "more"), (menu.Kind, menu.Text!, menu.Icon!));
        Assert.Equal([("Rename", "rename:" + Newest, "edit"), ("Archive", "archive:" + Newest, "archive")],
            menu.Children.Select(item => (item.Text!, item.Action!, item.Icon!)));
    }

    [Fact]
    public void SearchIsLiteralAndCaseInsensitive()
    {
        IReadOnlyList<ChatIndexEntry> chats = [Entry(Older, "xa.bx", 1), Entry(Newer, "aXb", 2), Entry(Newest, "(a+)*", 3)];
        Assert.Equal([Older], InoLayout.Filter(chats, " A.B ").Select(chat => chat.Id));
        Assert.Equal([Newest], InoLayout.Filter(chats, "(A+)*").Select(chat => chat.Id));
        Assert.Equal(3, InoLayout.Filter(chats, "  ").Count);
        var rows = Find(InoView.Render(App, new(chats, Older, "A.B")), "chats").Children;
        Assert.Equal(["xa.bx"], rows.Select(row => row.Children[0].Text));
    }

    [Fact]
    public void AnEmptySearchResultQuotesTheQuery()
    {
        Assert.Equal("No chats match \"Foo\".", Find(InoView.Render(App, new(Three(), Newer, "Foo")), "chats-empty").Text);
        // Text nodes are markdown: the query is escaped so it reads as typed.
        Assert.Equal("No chats match \"\\*x\\*\".", InoLayout.NoMatchCopy("*x*"));
    }

    [Fact]
    public void NoChatsShowsTheEmptyCopy()
        => Assert.Equal("Your chats will appear here.", Find(InoView.Render(App, new([], "main", "")), "chats-empty").Text);

    [Fact]
    public void RenamingShowsAModalWithATextFieldReference()
    {
        var root = InoView.Render(App, new(Three(), Newer, "", RenamingId: Newer));
        Assert.Equal(new[] { InoLayout.SidebarWidth, 0, InoLayout.OverlayExtent }, root.Extents);
        var modal = root.Children[2];
        Assert.Equal((UiNodeKind.Modal, "rename", "Rename chat"), (modal.Kind, modal.Key, modal.Text!));
        Assert.Equal(new UiChildRef(UIVocabulary.TextFieldType, App + "/rename"), modal.Children[0].Reference);
        Assert.Equal([("rename-save", "Save", UiStyle.Primary), ("rename-cancel", "Cancel", UiStyle.Ghost)],
            modal.Children.Skip(1).Select(button => (button.Action!, button.Text!, button.Style)));
        Assert.All(Flatten(root), node => Assert.InRange((int)node.Kind, 0, 13));
    }

    [Fact]
    public void ArchivingShowsAToastWithUndo()
    {
        var root = InoView.Render(App, new(Three(), Newer, "", Toast: "Chat archived.", UndoId: Older));
        var toast = root.Children[^1];
        Assert.Equal((UiNodeKind.Toast, "Chat archived.", UiTone.Neutral), (toast.Kind, toast.Text!, toast.Tone));
        var undo = toast.Children[0];
        Assert.Equal(("undo", "Undo", "undo-archive:" + Older, UiStyle.Link), (undo.Key, undo.Text!, undo.Action!, undo.Style));
        // The durable toast is stamped: a compose after its lifetime, or an unstamped one, drops it.
        var state = new InoViewState().Toasted(InoLayout.ArchivedToast, Older, Then);
        Assert.Equal("Chat archived.", state.Current(Then + InoLayout.ToastLifetime).Toast);
        Assert.Null(state.Current(Then + InoLayout.ToastLifetime + TimeSpan.FromSeconds(1)).Toast);
        Assert.Null(state.Current(Then + TimeSpan.FromHours(1)).UndoId);
        Assert.Null(new InoViewState(Toast: "unstamped").Current(Then).Toast);
    }

    [Fact]
    public void NarrowShowsARailWithNewChatAndAHistoryMenu()
    {
        var root = InoView.Render(App, new(Three(), Newer, "zzz"), narrow: true);
        Assert.Equal(new[] { InoLayout.RailWidth, 0 }, root.Extents);
        var rail = root.Children[0];
        Assert.Equal(["new", "history"], rail.Children.Select(node => node.Key));
        var add = rail.Children[0];
        Assert.Equal((UiNodeKind.Button, "plus", "New chat", "new"), (add.Kind, add.Icon!, add.Text!, add.Action!));
        var history = rail.Children[1];
        Assert.Equal((UiNodeKind.Menu, "Chats", "list"), (history.Kind, history.Text!, history.Icon!));
        Assert.Equal(["chat:" + Newest, "chat:" + Newer, "chat:" + Older], history.Children.Select(item => item.Action));
    }

    [Fact]
    public void ArchivingTheSelectedChatSelectsTheNewestRemaining()
    {
        var chats = Three();
        Assert.Equal(Newer, InoLayout.NextSelection(chats, Newest));
        Assert.Equal(Newest, InoLayout.NextSelection(chats, Older));
        Assert.Equal(Older, InoLayout.NextSelection([Entry(Older, "Older", 1), Entry(Newest, "Gone", 3, archived: true), Entry(Newer, "Newer", 2)], Newer));
        Assert.Null(InoLayout.NextSelection([Entry(Older, "Only", 1)], Older));
        Assert.Null(InoLayout.NextSelection([], Older));
    }

    [Fact]
    public void NodeKeysNeverCarryChatIds()
    {
        var chats = Three();
        foreach (var root in new[]
        {
            InoView.Render(App, new(chats, Newer, "", RenamingId: Newer, Toast: "Chat archived.", UndoId: Older)),
            InoView.Render(App, new(chats, Newer, ""), narrow: true),
        })
        {
            var keys = Flatten(root).Select(node => node.Key).ToArray();
            Assert.DoesNotContain(keys, key => chats.Any(chat => key.Contains(chat.Id[2..], StringComparison.Ordinal)));
        }
    }
}
