using System.Net.Http.Json;
using System.Text.Json;
using IntoChat.OS.Apps;
using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Text;
using IntoChat.OS.Flutter.Viewport;
using IntoChat.OS.Flutter.Views;
using IntoChat.OS.Flutter.Workspace;

namespace IntoChat.OS.Kernel.Tests.E2E;

// On the real script runtime: tabs inside windows (w-<n>, tabs by index), New tab and its preference,
// the window cap's toast, favourites, a 0.1.3 workspace's first 0.2.0 render, and Classic between sessions.
[Collection("Reference host")]
public sealed class DesktopTabFacts(ReferenceBrainFixture host) : BrainFact(host)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(90);
    private string Desktop => WorkspaceId + "/apps/desktop";
    private IWorkspace Workspace => Brain.Get<IWorkspace>(WorkspaceId);

    [Fact(Timeout = 360_000)]
    public async Task NewTabOpensTheDefaultAppInTheFocusedWindowAndSurvivesARestart()
    {
        var ct = TestContext.Current.CancellationToken;
        using var caller = User.AsCaller();
        var window = await TwoTabs(ct);
        var viewport = await TestWait.UntilAsync(_ => Brain.Get<IViewport>(Desktop + "/window/" + window).Read(),
            state => state.Target?.App == WorkspaceId + "/apps/calculator", Patience, ct);
        Assert.Equal("calculator", viewport.Target!.Id);
        var before = Assert.Single((await Workspace.Read()).Windows);

        await Restart(ct);
        var view = await Until(nodes => nodes.Any(node => node.Key == "tabs-" + window), ct);
        Assert.Equal(["Ino", "Calculator"], view.First(node => node.Key == "tabs-" + window).Children.Select(tab => tab.Text));
        var after = Assert.Single((await Workspace.Read()).Windows);
        Assert.Equal((before.Id, "calculator"), (after.Id, after.ActiveTabId));
        Assert.Equal(["ino", "calculator"], after.Tabs.Select(tab => tab.Id));
        Assert.Equal("calculator", (await Workspace.Read()).FocusedViewId);
    }

    [Fact(Timeout = 360_000)]
    public async Task SelectingATabSwitchesTheWindowsViewport()
    {
        var ct = TestContext.Current.CancellationToken;
        using var caller = User.AsCaller();
        var window = await TwoTabs(ct);

        await Click("tab-" + window + "-0", "tab:" + window + "/0", ct);
        await TestWait.UntilAsync(_ => Workspace.Read(), state => state.Windows.Single().ActiveTabId == "ino" && state.FocusedViewId == "ino", Patience, ct);
        await TestWait.UntilAsync(_ => Brain.Get<IViewport>(Desktop + "/window/" + window).Read(),
            state => state.Target?.App == WorkspaceId + "/apps/ino", Patience, ct);
        // The Tabs node marks the active tab Primary.
        await Until(found => found.Any(node => node.Key == "tab-" + window + "-0" && node.Style == UiStyle.Primary)
            && found.Any(node => node.Key == "tab-" + window + "-1" && node.Style == UiStyle.Ghost), ct);
    }

    [Fact(Timeout = 360_000)]
    public async Task ClosingTheLastTabClosesTheWindowAndClearsItsViewport()
    {
        var ct = TestContext.Current.CancellationToken;
        using var caller = User.AsCaller();
        var window = await TwoTabs(ct);

        // Closing the active (second) tab activates its left neighbour, and the viewport follows.
        await Click("close-" + window, "close:" + window + "/1", ct);
        await TestWait.UntilAsync(_ => Workspace.Read(), state => state.Windows.Single().Tabs.Length == 1 && state.Windows.Single().ActiveTabId == "ino", Patience, ct);
        await TestWait.UntilAsync(_ => Brain.Get<IViewport>(Desktop + "/window/" + window).Read(),
            state => state.Target?.App == WorkspaceId + "/apps/ino", Patience, ct);

        await Click("close-" + window, "close:" + window + "/0", ct);
        await TestWait.UntilAsync(_ => Workspace.Read(), state => state.Windows.Length == 0 && state.OpenViews.Length == 0, Patience, ct);
        await TestWait.UntilAsync(_ => Brain.Get<IViewport>(Desktop + "/window/" + window).Read(), state => state.Target is null, Patience, ct);
        await Until(nodes => nodes.All(node => node.Key != "frame-" + window), ct);
    }

    [Fact(Timeout = 360_000)]
    public async Task TheNinthWindowShowsTheToastAndChangesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var caller = User.AsCaller();
        await Start(ct);
        // Eight single-tab windows of Ino views; the calculator is not open.
        WorkspaceWindow[] eight = [.. Enumerable.Range(1, 8).Select(n => new WorkspaceWindow("w-" + n, [Ino(n == 1 ? "ino" : "ino-" + n)], n == 1 ? "ino" : "ino-" + n))];
        var full = await Apply(new WorkspaceUpdate([.. eight.SelectMany(window => window.Tabs)], "ino-8", []) { Windows = eight }, ct);
        await Until(nodes => nodes.Any(node => node.Key == "frame-w-8"), ct);

        await Click("gallery", "gallery", ct);
        await ClickAction("open:calculator", ct);
        var refused = await Until(nodes => nodes.Any(node => node.Key == "toast"), ct);
        Assert.Equal("You can have at most 8 windows. Close one to open another.", refused.Single(node => node.Key == "toast").Text);
        var unchanged = await Workspace.Read();
        Assert.Equal(full.Revision, unchanged.Revision);
        Assert.Equal(8, unchanged.Windows.Length);
        Assert.DoesNotContain(unchanged.OpenViews, view => view.Id == "calculator");
        // Open is not Get: the gallery shows no notice of its own.
        Assert.DoesNotContain(refused, node => node.Key == "notice");

        // A change made outside the desktop (Classic, another app) moves the revision: the toast hides.
        await Apply(new WorkspaceUpdate(unchanged.OpenViews, "ino", unchanged.Pins) { Windows = unchanged.Windows }, ct);
        await Until(nodes => nodes.All(node => node.Key != "toast"), ct);
    }

    [Fact(Timeout = 360_000)]
    public async Task StarringAWindowAddsAFavouriteToTheDockAndStarringAgainRemovesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        using var caller = User.AsCaller();
        var window = await TwoTabs(ct);
        await Until(nodes => nodes.Any(node => node.Key == "star-" + window) && nodes.All(node => node.Key != "favourites"), ct);

        await Click("star-" + window, "favourite:" + window, ct);
        var starred = await TestWait.UntilAsync(_ => Workspace.Read(), state => state.Favourites.Length == 1, Patience, ct);
        var favourite = Assert.Single(starred.Favourites);
        Assert.Equal(("calculator", WorkspaceId + "/apps/calculator", ""), (favourite.Id, favourite.App, favourite.Variant));
        var dock = await Until(nodes => nodes.Any(node => node.Key == "favourites"), ct);
        var entry = Assert.Single(dock.Single(node => node.Key == "favourites").Children);
        Assert.Equal(("Calculator", "open:calculator"), (entry.Text, entry.Action));

        await Click("star-" + window, "favourite:" + window, ct);
        await TestWait.UntilAsync(_ => Workspace.Read(), state => state.Favourites.Length == 0, Patience, ct);
        await Until(nodes => nodes.All(node => node.Key != "favourites"), ct);
    }

    [Fact(Timeout = 360_000)]
    public async Task ADesktopFrom013ShowsItsOpenViewsAsSingleTabWindows()
    {
        var ct = TestContext.Current.CancellationToken;
        using var caller = User.AsCaller();
        await Brain.Get<IText>(Desktop + "/greeted").Set("true");
        // 0.1.3 stored open views only, and kept one viewport per view at /window/<view id>.
        await Apply(new WorkspaceUpdate([Ino("ino"), Calculator("calculator")], "calculator", []), ct);
        var old = Brain.Get<IViewport>(Desktop + "/window/calculator");
        await old.Set(Calculator("calculator"), (await old.Read()).Revision);

        await Start(ct);
        var state = await TestWait.UntilAsync(_ => Workspace.Read(), value => value.Windows.Length == 2, Patience, ct);
        Assert.Equal(["w-1", "w-2"], state.Windows.Select(window => window.Id));
        Assert.Equal([["ino"], ["calculator"]], state.Windows.Select(window => window.Tabs.Select(tab => tab.Id).ToArray()));
        Assert.Equal("calculator", state.FocusedViewId);
        await TestWait.UntilAsync(_ => Brain.Get<IViewport>(Desktop + "/window/w-1").Read(), value => value.Target?.App == WorkspaceId + "/apps/ino", Patience, ct);
        await TestWait.UntilAsync(_ => Brain.Get<IViewport>(Desktop + "/window/w-2").Read(), value => value.Target?.App == WorkspaceId + "/apps/calculator", Patience, ct);
        await TestWait.UntilAsync(_ => old.Read(), value => value.Target is null, Patience, ct);
        await Until(nodes => nodes.Any(node => node.Key == "frame-w-1") && nodes.Any(node => node.Key == "frame-w-2"), ct);
    }

    [Fact(Timeout = 360_000)]
    public async Task ClassicStillOpensAndClosesViewsAlongsideTabs()
    {
        var ct = TestContext.Current.CancellationToken;
        using var caller = User.AsCaller();
        var window = await TwoTabs(ct);
        var other = (await Preferences(ct)).GetProperty("choices").EnumerateArray().Select(choice => choice.GetProperty("name").GetString()!)
            .First(name => name is not ("ino" or "calculator" or "circle"));

        // Classic: open another app there and close the calculator tab.
        await Brain.Get<IText>(Desktop + "/layout").Set("classic");
        await Connect(ct);
        await Navigate("open", other, ct);
        await TestWait.UntilAsync(_ => Workspace.Read(), state => state.OpenViews.Any(view => view.Id == other), Patience, ct);
        await Navigate("close", "calculator", ct);
        await TestWait.UntilAsync(_ => Workspace.Read(), state => state.OpenViews.All(view => view.Id != "calculator"), Patience, ct);

        await Brain.Get<IText>(Desktop + "/layout").Set("desktop");
        await Connect(ct);
        var state = await Workspace.Read();
        var kept = state.Windows.Single(w => w.Id == window);
        Assert.Equal(["ino"], kept.Tabs.Select(tab => tab.Id));
        Assert.Equal("ino", kept.ActiveTabId);
        var added = state.Windows.Single(w => w.Id != window);
        Assert.Equal([other], added.Tabs.Select(tab => tab.Id));
        Assert.Equal(other, added.ActiveTabId);
        // Nothing orphaned: every tab is an open view and every open view is in a window.
        Assert.Equal(state.OpenViews.Select(view => view.Id).Order(), state.Windows.SelectMany(w => w.Tabs).Select(tab => tab.Id).Order());
        await Until(nodes => nodes.Any(node => node.Key == "frame-" + window) && nodes.Any(node => node.Key == "frame-" + added.Id), ct);
        await TestWait.UntilAsync(_ => Brain.Get<IViewport>(Desktop + "/window/" + window).Read(), value => value.Target?.App == WorkspaceId + "/apps/ino", Patience, ct);
    }

    // A started desktop with no windows; `newtab` gives one window holding Ino, then with the preference
    // set to the calculator, the window's + adds it as the active tab. Returns the window's id.
    private async Task<string> TwoTabs(CancellationToken ct)
    {
        await Start(ct);
        await Click("newtab", "newtab", ct);
        var first = await TestWait.UntilAsync(_ => Workspace.Read(), state => state.Windows.Length == 1, Patience, ct);
        var window = Assert.Single(first.Windows);
        Assert.Equal(["ino"], window.Tabs.Select(tab => tab.Id));
        Assert.Equal("ino", window.ActiveTabId);
        Assert.StartsWith("w-", window.Id, StringComparison.Ordinal);

        using (var put = await User.Http.PutAsJsonAsync($"/brains/{WorkspaceId}/preferences/workspace", new { newTabApp = "calculator" }, ct)) { put.EnsureSuccessStatusCode(); }
        Assert.Equal("calculator", (await Preferences(ct)).GetProperty("newTabApp").GetString());
        await Click("newtab-" + window.Id, "newtab:" + window.Id, ct);
        var second = await TestWait.UntilAsync(_ => Workspace.Read(), state => state.Windows.Single().Tabs.Length == 2, Patience, ct);
        Assert.Equal(["ino", "calculator"], second.Windows.Single().Tabs.Select(tab => tab.Id));
        Assert.Equal(("calculator", "calculator"), (second.Windows.Single().ActiveTabId, second.FocusedViewId));
        return window.Id;
    }

    private async Task<JsonElement> Preferences(CancellationToken ct)
        => (await User.Http.GetFromJsonAsync<JsonElement>($"/brains/{WorkspaceId}/preferences/workspace", ct)).Clone();

    private WorkspaceViewRef Ino(string id) => new(id, WorkspaceId + "/apps/ino", "full", "");
    private WorkspaceViewRef Calculator(string id) => new(id, WorkspaceId + "/apps/calculator", "full", "");

    // Only the shipped workspace and desktop apps may apply navigation state: apply as the desktop does.
    private async Task<WorkspaceState> Apply(WorkspaceUpdate update, CancellationToken ct)
    {
        var revision = (await Workspace.Read()).Revision;
        using var app = User.AsApp(Desktop);
        return await Workspace.Apply(update, "fact:" + Guid.NewGuid(), revision).WaitAsync(ct);
    }

    // Classic's own navigation, as its launcher and commands send it.
    private async Task Navigate(string action, string id, CancellationToken ct)
    {
        var revision = (await Workspace.Read()).Revision;
        await Brain.Get<IApp>(WorkspaceId + "/apps/workspace").Request("navigate", JsonSerializer.Serialize(new { revision, action, id })).WaitAsync(ct);
    }

    // Skips Ino's first-run window, so each fact starts with no windows.
    private async Task Start(CancellationToken ct)
    {
        await Brain.Get<IText>(Desktop + "/greeted").Set("true");
        await Restart(ct);
    }

    private async Task Restart(CancellationToken ct)
    {
        var before = (await Brain.Get<IView>(Desktop + "/view").Read())?.Generation ?? -1;
        await Brain.Get<IText>(Desktop + "/layout").Set("desktop");
        await Brain.Get<IApp>(Desktop).Configure(new(), []);
        await AppRuntimeReadiness.WaitAsync(Brain, User, ct);
        await Connect(ct);
        await TestWait.UntilAsync(_ => Brain.Get<IView>(Desktop + "/view").Read(), view => view is not null && view.Generation > before, Patience, ct);
    }

    private async Task Connect(CancellationToken ct)
    {
        using var connected = await User.Http.PostAsJsonAsync($"/brains/{WorkspaceId}/apps/connect", new { }, ct);
        connected.EnsureSuccessStatusCode();
    }

    private async Task ClickAction(string action, CancellationToken ct)
    {
        var view = await TestWait.UntilAsync(_ => Brain.Get<IView>(Desktop + "/view").Read(),
            published => published is not null && Flatten(published.Root).Any(node => node.Action == action && node.Kind == UiNodeKind.Button), Patience, ct);
        var key = Flatten(view!.Root).First(node => node.Action == action && node.Kind == UiNodeKind.Button).Key;
        await Brain.Get<IView>(Desktop + "/view").Activate(view.Generation, key, action, Guid.NewGuid());
    }

    private async Task Click(string key, string action, CancellationToken ct)
    {
        var view = await TestWait.UntilAsync(_ => Brain.Get<IView>(Desktop + "/view").Read(),
            published => published is not null && Flatten(published.Root).Any(node => node.Key == key && node.Action == action), Patience, ct);
        await Brain.Get<IView>(Desktop + "/view").Activate(view!.Generation, key, action, Guid.NewGuid());
    }

    private async Task<UiNode[]> Until(Func<UiNode[], bool> ready, CancellationToken ct)
    {
        try
        {
            var view = await TestWait.UntilAsync(_ => Brain.Get<IView>(Desktop + "/view").Read(),
                published => published is not null && ready([.. Flatten(published.Root)]), Patience, ct);
            return [.. Flatten(view!.Root)];
        }
        catch (TimeoutException)
        {
            var last = await Brain.Get<IView>(Desktop + "/view").Read();
            throw new TimeoutException("The desktop showed: " + string.Join(", ", last is null ? [] : Flatten(last.Root).Select(node => node.Key)));
        }
    }

    private static IEnumerable<UiNode> Flatten(UiNode root)
    {
        yield return root;
        foreach (var child in root.Children.SelectMany(Flatten)) { yield return child; }
    }
}
