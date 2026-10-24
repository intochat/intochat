using IntoChat.OS.Flutter.Workspace;

namespace IntoChat.OS.Kernel.Tests.Unit;

// The pure window, tab and favourite rules behind every desktop tab operation.
public sealed class OsDesktopWindowOpsFacts
{
    private static WorkspaceViewRef V(string id, string app, string variant = "full", string context = "") => new(id, app, variant, context);

    private static WorkspaceWindow W(string id, string active, params WorkspaceViewRef[] tabs) => new(id, tabs, active);

    private static WorkspaceState S(string? focus, params WorkspaceWindow[] windows) => new(0, [], [])
    {
        Windows = windows,
        OpenViews = [.. windows.SelectMany(window => window.Tabs)],
        FocusedViewId = focus,
    };

    private static string Name(string app) => app;

    // Every update keeps OpenViews equal to the flattened tabs, in window order.
    private static WorkspaceUpdate Ok(WindowChange change)
    {
        Assert.Null(change.Refusal);
        var update = Assert.IsType<WorkspaceUpdate>(change.Update);
        Assert.NotNull(update.Windows);
        Assert.Equal(update.Windows.SelectMany(window => window.Tabs), update.OpenViews);
        // The server's CheckViews and CheckFavourites rules: non-blank, at most 128 chars, unique.
        foreach (var views in new[] { update.OpenViews, update.Favourites ?? [] })
        {
            Assert.All(views, view => Assert.False(string.IsNullOrWhiteSpace(view.Id) || view.Id.Length > 128, view.Id));
            Assert.Equal(views.Length, views.Select(view => view.Id).Distinct(StringComparer.Ordinal).Count());
        }
        return update;
    }

    private static WorkspaceState Apply(WorkspaceState s, WorkspaceUpdate u) => s with
    {
        OpenViews = u.OpenViews,
        FocusedViewId = u.FocusedViewId,
        Pins = u.Pins,
        Windows = u.Windows ?? s.Windows,
        Favourites = u.Favourites ?? s.Favourites,
    };

    [Fact]
    public void OpeningAnAppAlreadyInATabSelectsThatTab()
    {
        var s = S("notes", W("w-1", "ino", V("ino", "ino"), V("files", "files")), W("w-2", "notes", V("notes", "notes")));
        var u = Ok(WindowOps.OpenApp(s, "files", V("files", "files")));
        Assert.Equal("files", u.Windows![0].ActiveTabId);
        Assert.Equal("files", u.FocusedViewId);
        Assert.Equal(2, u.Windows.Length);
    }

    [Fact]
    public void OpeningANewAppAddsAWindowBesideTheOthers()
    {
        var s = S("ino", W("w-1", "ino", V("ino", "ino")));
        var u = Ok(WindowOps.OpenApp(s, "notes", V("notes", "notes")));
        Assert.Equal(["w-1", "w-2"], u.Windows!.Select(window => window.Id));
        Assert.Equal([V("notes", "notes")], u.Windows![1].Tabs);
        Assert.Equal("notes", u.Windows[1].ActiveTabId);
        Assert.Equal("notes", u.FocusedViewId);
    }

    [Fact]
    public void TheNinthWindowIsRefusedWithTheWindowsToast()
    {
        var s = S(null, [.. Enumerable.Range(1, 8).Select(i => W("w-" + i, "a" + i, V("a" + i, "a" + i)))]);
        var change = WindowOps.OpenApp(s, "z", V("z", "z"));
        Assert.Null(change.Update);
        Assert.Equal("You can have at most 8 windows. Close one to open another.", change.Refusal);
    }

    [Fact]
    public void NewTabAddsAnActiveTabToTheTargetWindow()
    {
        var s = S("ino", W("w-1", "ino", V("ino", "ino")), W("w-2", "notes", V("notes", "notes")));
        var u = Ok(WindowOps.NewTab(s, "w-2", "files", V("files", "files")));
        Assert.Equal(["notes", "files"], u.Windows![1].Tabs.Select(tab => tab.Id));
        Assert.Equal("files", u.Windows[1].ActiveTabId);
        Assert.Equal("files", u.FocusedViewId);
        // Without a window id, the focused window is the target.
        var focused = Ok(WindowOps.NewTab(s, null, "files", V("files", "files")));
        Assert.Equal(["ino", "files"], focused.Windows![0].Tabs.Select(tab => tab.Id));
    }

    [Fact]
    public void NewTabWithoutWindowsOpensOneWindow()
    {
        var u = Ok(WindowOps.NewTab(S(null), null, "ino", V("ino", "ino")));
        var window = Assert.Single(u.Windows!);
        Assert.Equal("w-1", window.Id);
        Assert.Equal("ino", window.ActiveTabId);
        Assert.Equal("ino", u.FocusedViewId);
    }

    [Fact]
    public void TheNinthTabIsRefusedWithTheTabsToast()
    {
        var s = S("t1", W("w-1", "t1", [.. Enumerable.Range(1, 8).Select(i => V("t" + i, "t" + i))]));
        var change = WindowOps.NewTab(s, "w-1", "z", V("z", "z"));
        Assert.Null(change.Update);
        Assert.Equal("A window holds at most 8 tabs.", change.Refusal);
    }

    [Fact]
    public void SecondTabOfTheSameAppGetsASuffixedId()
    {
        var s = S("ino", W("w-1", "ino", V("ino", "ino")));
        Assert.Equal("ino-2", WindowOps.TabId(s, "ino"));
        s = Apply(s, Ok(WindowOps.NewTab(s, "w-1", "ino", V("ino", "ino"))));
        Assert.Equal(["ino", "ino-2"], s.Windows[0].Tabs.Select(tab => tab.Id));
        s = Apply(s, Ok(WindowOps.NewTab(s, "w-1", "ino", V("ino", "ino"))));
        Assert.Equal(["ino", "ino-2", "ino-3"], s.Windows[0].Tabs.Select(tab => tab.Id));
        Assert.Equal("notes", WindowOps.TabId(s, "notes"));
    }

    [Fact]
    public void SelectingATabMakesItActiveAndFocusesItsWindow()
    {
        var s = S("notes", W("w-1", "ino", V("ino", "ino"), V("a/b c", "files")), W("w-2", "notes", V("notes", "notes")));
        var u = Ok(WindowOps.SelectTab(s, "w-1", "a/b c"));
        Assert.Equal("a/b c", u.Windows![0].ActiveTabId);
        Assert.Equal("a/b c", u.FocusedViewId);
        var noop = WindowOps.SelectTab(s, "w-2", "notes");
        Assert.Null(noop.Update);
        Assert.Null(noop.Refusal);
        Assert.Null(WindowOps.SelectTab(s, "w-1", "missing").Update);
        Assert.Equal("ino", Ok(WindowOps.Focus(s, "w-1")).FocusedViewId);
    }

    [Fact]
    public void ClosingTheActiveTabActivatesItsLeftNeighbour()
    {
        var s = S("c", W("w-1", "c", V("a", "a"), V("b", "b"), V("c", "c")));
        var u = Ok(WindowOps.CloseTab(s, "w-1", "c"));
        Assert.Equal("b", u.Windows![0].ActiveTabId);
        Assert.Equal("b", u.FocusedViewId);
        var first = Ok(WindowOps.CloseTab(S("a", W("w-1", "a", V("a", "a"), V("b", "b"))), "w-1", "a"));
        Assert.Equal("b", first.Windows![0].ActiveTabId);
    }

    [Fact]
    public void ClosingAnInactiveTabKeepsTheActiveOne()
    {
        var u = Ok(WindowOps.CloseTab(S("c", W("w-1", "c", V("a", "a"), V("b", "b"), V("c", "c"))), "w-1", "a"));
        Assert.Equal(["b", "c"], u.Windows![0].Tabs.Select(tab => tab.Id));
        Assert.Equal("c", u.Windows[0].ActiveTabId);
        Assert.Equal("c", u.FocusedViewId);
    }

    [Fact]
    public void ClosingTheLastTabRemovesTheWindowAndMovesFocus()
    {
        var s = S("notes", W("w-1", "files", V("ino", "ino"), V("files", "files")), W("w-2", "notes", V("notes", "notes")));
        var u = Ok(WindowOps.CloseTab(s, "w-2", "notes"));
        Assert.Equal(["w-1"], u.Windows!.Select(window => window.Id));
        Assert.Equal("files", u.FocusedViewId);
        var empty = Ok(WindowOps.CloseTab(S("ino", W("w-1", "ino", V("ino", "ino"))), "w-1", "ino"));
        Assert.Empty(empty.Windows!);
        Assert.Null(empty.FocusedViewId);
    }

    [Fact]
    public void StarringAFullTabFavouritesItsAppAndStarringAgainRemovesIt()
    {
        var s = S("notes-2", W("w-1", "notes-2", V("notes-2", "notes")));
        var u = Ok(WindowOps.ToggleFavourite(s, "w-1", app => "Notes"));
        Assert.Equal([new WorkspaceViewRef("Notes", "notes", "", "")], u.Favourites!);
        s = Apply(s, u);
        Assert.True(WindowOps.IsFavourite(s, s.Windows[0].Tabs[0]));
        var again = Ok(WindowOps.ToggleFavourite(s, "w-1", app => "Notes"));
        Assert.Empty(again.Favourites!);
    }

    [Fact]
    public void StarringAViewTabFavouritesTheView()
    {
        var s = S("tab", W("w-1", "tab", V("tab", "ino", "thread", "t1")));
        var u = Ok(WindowOps.ToggleFavourite(s, "w-1", Name));
        Assert.Equal([new WorkspaceViewRef("v-tab", "ino", "thread", "t1")], u.Favourites!);
        Assert.True(WindowOps.IsFavourite(Apply(s, u), V("other", "ino", "thread", "t1")));
        Assert.False(WindowOps.IsFavourite(Apply(s, u), V("other", "ino")));
    }

    [Fact]
    public void TheSixthFavouriteAppIsRefusedButASixthItemThatIsAViewIsNot()
    {
        var apps = Enumerable.Range(1, 5).Select(i => new WorkspaceViewRef("a" + i, "a" + i, "", "")).ToArray();
        var view = S("tab", W("w-1", "tab", V("tab", "ino", "thread", "t1"))) with { Favourites = apps };
        Assert.Equal(6, Ok(WindowOps.ToggleFavourite(view, "w-1", Name)).Favourites!.Length);
        var app = S("z", W("w-1", "z", V("z", "z"))) with { Favourites = apps };
        var refused = WindowOps.ToggleFavourite(app, "w-1", Name);
        Assert.Null(refused.Update);
        Assert.Equal("Favourites hold at most 5 apps.", refused.Refusal);
        var views = Enumerable.Range(1, 5).Select(i => new WorkspaceViewRef("v" + i, "ino", "thread", "x" + i)).ToArray();
        var full = S("tab", W("w-1", "tab", V("tab", "ino", "thread", "t1"))) with { Favourites = views };
        Assert.Equal("Favourites hold at most 5 views.", WindowOps.ToggleFavourite(full, "w-1", Name).Refusal);
    }

    [Fact]
    public void FavouritingAReopenedTabWithADifferentContextGetsAFreshId()
    {
        var kept = new WorkspaceViewRef("v-ino", "ino", "thread", "t1");
        var s = S("ino", W("w-1", "ino", V("ino", "ino", "thread", "t2"))) with { Favourites = [kept] };
        var u = Ok(WindowOps.ToggleFavourite(s, "w-1", Name));
        Assert.Equal([kept, new WorkspaceViewRef("v-ino-2", "ino", "thread", "t2")], u.Favourites!);
    }

    [Fact]
    public void ALongTabIdGivesAFavouriteIdOfAtMost128Chars()
    {
        var id = new string('x', 128);
        var s = S(id, W("w-1", id, V(id, "ino", "thread", "t1")));
        var u = Ok(WindowOps.ToggleFavourite(s, "w-1", Name));
        Assert.Equal(128, Assert.Single(u.Favourites!).Id.Length);
        var taken = s with { Favourites = [new WorkspaceViewRef(("v-" + id)[..128], "ino", "thread", "other")] };
        Ok(WindowOps.ToggleFavourite(taken, "w-1", Name));
    }

    [Fact]
    public void AnAppFavouriteWhoseNameCollidesGetsASuffix()
    {
        var s = S("notes", W("w-1", "notes", V("notes", "notes"))) with { Favourites = [new WorkspaceViewRef("Notes", "ino", "thread", "t1")] };
        var u = Ok(WindowOps.ToggleFavourite(s, "w-1", _ => "Notes"));
        Assert.Equal(new WorkspaceViewRef("Notes-2", "notes", "", ""), u.Favourites![1]);
    }

    [Fact]
    public void ABlankAppNameFallsBackToTheAppKey()
    {
        var s = S("notes", W("w-1", "notes", V("notes", "apps/notes")));
        Assert.Equal("apps/notes", Ok(WindowOps.ToggleFavourite(s, "w-1", _ => "  ")).Favourites![0].Id);
    }

    [Fact]
    public void UnknownWindowsAreNoOps()
    {
        var s = S("ino", W("w-1", "ino", V("ino", "ino")));
        Assert.Equal(new WindowChange(null, null), WindowOps.Focus(s, "w-9"));
        // An explicit window that no longer exists is a stale action: nothing opens anywhere.
        Assert.Equal(new WindowChange(null, null), WindowOps.NewTab(s, "w-9", "notes", V("notes", "notes")));
    }

    [Fact]
    public void ClosingATabInAnUnfocusedWindowKeepsTheFocus()
    {
        var s = S("notes", W("w-1", "a", V("a", "a"), V("b", "b")), W("w-2", "notes", V("notes", "notes")), W("w-3", "c", V("c", "c")));
        Assert.Equal("notes", Ok(WindowOps.CloseTab(s, "w-1", "a")).FocusedViewId);
        Assert.Equal("notes", Ok(WindowOps.CloseTab(s, "w-3", "c")).FocusedViewId);
    }
}
