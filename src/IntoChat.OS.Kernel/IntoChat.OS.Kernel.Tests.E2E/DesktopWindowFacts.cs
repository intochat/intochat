using System.Net.Http.Json;
using IntoChat.OS.Apps;
using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Viewport;
using IntoChat.OS.Flutter.Views;
using IntoChat.OS.Flutter.Workspace;

namespace IntoChat.OS.Kernel.Tests.E2E;

// On the real script runtime: the desktop's open/focus/close synapses change the durable
// workspace, restore.cs re-renders, and a restarted desktop brings the same windows back.
[Collection("Reference host")]
public sealed class DesktopWindowFacts(ReferenceBrainFixture host) : BrainFact(host)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(90);

    [Fact(Timeout = 360_000)]
    public async Task WindowsOpenSideBySideFocusCloseAndSurviveARestart()
    {
        var ct = TestContext.Current.CancellationToken;
        using var caller = User.AsCaller();
        var desktop = WorkspaceId + "/apps/desktop";
        var workspace = Brain.Get<IWorkspace>(WorkspaceId);
        await Brain.Get<IntoChat.OS.Flutter.Text.IText>(desktop + "/layout").Set("desktop");
        await Brain.Get<IApp>(desktop).Configure(new(), []);
        await AppRuntimeReadiness.WaitAsync(Brain, User, ct);
        await Connect(ct);
        // The dock is narrow (Ino, pins, open apps): other installed apps open from the gallery.
        await Click(desktop, "gallery", "gallery", ct);
        var gallery = await Until(desktop, nodes => nodes.Any(node => node.Action == "open:calculator"), ct);
        // Any second installed app beside the calculator.
        var other = gallery.First(node => node.Action is { } action && action.StartsWith("open:", StringComparison.Ordinal)
            && action is not ("open:calculator" or "open:ino" or "open:circle")).Action!["open:".Length..];

        await ClickAction(desktop, "open:calculator", ct);
        await ClickAction(desktop, "open:" + other, ct);
        await Click(desktop, "gallery-close", "gallery", ct);
        var both = await Until(desktop, nodes => Has(nodes, "calculator") && Has(nodes, other), ct);
        Assert.Equal([UIVocabulary.ViewportType], both.Where(node => node.Key == "content-calculator" || node.Key == "content-" + other).Select(node => node.Reference!.Kind).Distinct());
        var target = await TestWait.UntilAsync(_ => Brain.Get<IViewport>(desktop + "/window/calculator").Read(),
            state => state.Target is not null, Patience, ct);
        Assert.Equal(WorkspaceId + "/apps/calculator", target.Target!.App);
        var second = await TestWait.UntilAsync(_ => Brain.Get<IViewport>(desktop + "/window/" + other).Read(),
            state => state.Target is not null, Patience, ct);
        Assert.Equal(WorkspaceId + "/apps/" + other, second.Target!.App);
        Assert.Equal(other, (await workspace.Read()).FocusedViewId);

        await Click(desktop, "focus-calculator", "focus:calculator", ct);
        await TestWait.UntilAsync(_ => workspace.Read(), state => state.FocusedViewId == "calculator", Patience, ct);

        await Click(desktop, "close-" + other, "close:" + other, ct);
        await Until(desktop, nodes => Has(nodes, "calculator") && !Has(nodes, other), ct);
        await TestWait.UntilAsync(_ => Brain.Get<IViewport>(desktop + "/window/" + other).Read(), state => state.Target is null, Patience, ct);

        // Restart: a new program generation re-runs start.cs, which reads the durable workspace.
        var before = (await Brain.Get<IView>(desktop + "/view").Read())!.Generation;
        await Brain.Get<IntoChat.OS.Flutter.Text.IText>(desktop + "/layout").Set("desktop");
        await Brain.Get<IApp>(desktop).Configure(new(), []);
        await AppRuntimeReadiness.WaitAsync(Brain, User, ct);
        await Connect(ct);
        var restored = await TestWait.UntilAsync(_ => Brain.Get<IView>(desktop + "/view").Read(),
            view => view!.Generation > before && Has([.. Flatten(view.Root)], "calculator"), Patience, ct);
        Assert.DoesNotContain(Flatten(restored!.Root), node => node.Key == "content-" + other);
        Assert.Equal("calculator", (await workspace.Read()).FocusedViewId);
    }

    [Fact(Timeout = 360_000)]
    public async Task PinningKeepsAnAppInTheWorkspacePinsAndUnpinningRemovesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        using var caller = User.AsCaller();
        var desktop = WorkspaceId + "/apps/desktop";
        var workspace = Brain.Get<IWorkspace>(WorkspaceId);
        await Brain.Get<IntoChat.OS.Flutter.Text.IText>(desktop + "/layout").Set("desktop");
        await Brain.Get<IApp>(desktop).Configure(new(), []);
        await AppRuntimeReadiness.WaitAsync(Brain, User, ct);
        await Connect(ct);
        await Click(desktop, "gallery", "gallery", ct);
        await ClickAction(desktop, "open:calculator", ct);
        await Click(desktop, "gallery-close", "gallery", ct);

        await Click(desktop, "pin-calculator", "pin:calculator", ct);
        await TestWait.UntilAsync(_ => workspace.Read(), state => state.Pins.Any(view => view.Id == "calculator"), Patience, ct);
        var dock = await Until(desktop, nodes => nodes.Any(node => node.Key == "unpin-calculator"), ct);
        Assert.Equal("dock-ino", dock.First(node => node.Key.StartsWith("dock-", StringComparison.Ordinal)).Key);

        await Click(desktop, "unpin-calculator", "unpin:calculator", ct);
        await TestWait.UntilAsync(_ => workspace.Read(), state => state.Pins.All(view => view.Id != "calculator"), Patience, ct);
    }

    [Fact(Timeout = 360_000)]
    public async Task GettingAnotherAccountsAppInstallsItAndOpensItInAWindow()
    {
        var ct = TestContext.Current.CancellationToken;
        var desktop = WorkspaceId + "/apps/desktop";
        // Another account publishes two apps this user does not have.
        var name = "echo" + Guid.NewGuid().ToString("N")[..8];
        var clash = "clash" + Guid.NewGuid().ToString("N")[..8];
        using (var author = await Brain.PersonAsync("author-" + Guid.NewGuid().ToString("N"), ct))
        using (author.AsCaller())
        {
            foreach (var published in new[] { name, clash })
            {
                var app = Brain.Get<IApp>(IApp.Key(author.BrainId, published));
                await app.Create(new("Echo " + published, "echoes", [new("ask", "ask it")], [], []), "## Scenario: Echoes\nIt echoes.");
                await app.BeginBuild(null);
                await app.CompleteBuild(await app.CurrentBuildAttempt(), new() { ["synapses/echo.cs"] = Echo }, null);
                await app.Publish();
            }
        }
        using var caller = User.AsCaller();
        await Brain.Get<IntoChat.OS.Flutter.Text.IText>(desktop + "/layout").Set("desktop");
        await Brain.Get<IApp>(desktop).Configure(new(), []);
        await AppRuntimeReadiness.WaitAsync(Brain, User, ct);
        await Connect(ct);
        await Click(desktop, "gallery", "gallery", ct);
        var gallery = await Until(desktop, nodes => nodes.Any(node => node.Action?.EndsWith("/" + name, StringComparison.Ordinal) == true), ct);
        var get = gallery.First(node => node.Action?.EndsWith("/" + name, StringComparison.Ordinal) == true);
        Assert.Equal(("Get", true), (get.Text, get.Action!.StartsWith("get:", StringComparison.Ordinal)));

        await ClickAction(desktop, get.Action, ct);
        var window = await TestWait.UntilAsync(_ => Brain.Get<IViewport>(desktop + "/window/" + name).Read(), state => state.Target is not null, Patience, ct);
        Assert.Equal(WorkspaceId + "/apps/" + name, window.Target!.App);
        Assert.Equal(name, (await Brain.Get<IWorkspace>(WorkspaceId).Read()).FocusedViewId);
        // The entry now offers Open.
        await Until(desktop, nodes => nodes.Any(node => node.Action == "open:" + name && node.Text == "Open"), ct);

        // The user then makes an app of their own under the second name, while its Get is still shown:
        // getting the listing is refused with a notice, and the gallery stays.
        var getClash = get.Action[..get.Action.LastIndexOf('/')] + "/" + clash;
        await Until(desktop, nodes => nodes.Any(node => node.Action == getClash), ct);
        await Brain.Get<IApp>(IApp.Key(WorkspaceId, clash)).Create(new("Mine", "mine", [new("ask", "ask it")], [], []), "## Scenario: Mine\nIt is mine.");
        await ClickAction(desktop, getClash, ct);
        var refused = await Until(desktop, nodes => nodes.Any(node => node.Key == "notice"), ct);
        Assert.False(string.IsNullOrWhiteSpace(Assert.Single(refused, node => node.Key == "notice").Text));
        Assert.Contains(refused, node => node.Key == "gallery-panel");
    }

    // A minimal real synapse: it answers "ask" requests by doing nothing.
    private const string Echo = """
        using IntoChat.OS.Apps;
        using IntoChat.OS.Client;
        await using var brain = await IntoChatOSClient.ConnectAsync(args);
        await foreach (var request in brain.On(brain.Get<IApp>(brain.Setting("App")!), "ask", brain.Stopping)) { }
        """;

    private async Task ClickAction(string desktop, string action, CancellationToken ct)
    {
        var view = await TestWait.UntilAsync(_ => Brain.Get<IView>(desktop + "/view").Read(),
            published => published is not null && Flatten(published.Root).Any(node => node.Action == action && node.Kind == UiNodeKind.Button), Patience, ct);
        var key = Flatten(view!.Root).First(node => node.Action == action && node.Kind == UiNodeKind.Button).Key;
        await Brain.Get<IView>(desktop + "/view").Activate(view.Generation, key, action, Guid.NewGuid());
    }

    private async Task Connect(CancellationToken ct)
    {
        using var connected = await User.Http.PostAsJsonAsync($"/brains/{WorkspaceId}/apps/connect", new { }, ct);
        connected.EnsureSuccessStatusCode();
    }

    private async Task Click(string desktop, string key, string action, CancellationToken ct)
    {
        var view = await TestWait.UntilAsync(_ => Brain.Get<IView>(desktop + "/view").Read(),
            published => published is not null && Flatten(published.Root).Any(node => node.Key == key && node.Action == action), Patience, ct);
        await Brain.Get<IView>(desktop + "/view").Activate(view!.Generation, key, action, Guid.NewGuid());
    }

    private async Task<UiNode[]> Until(string desktop, Func<UiNode[], bool> ready, CancellationToken ct)
    {
        try
        {
            var view = await TestWait.UntilAsync(_ => Brain.Get<IView>(desktop + "/view").Read(),
                published => published is not null && ready([.. Flatten(published.Root)]), Patience, ct);
            return [.. Flatten(view!.Root)];
        }
        catch (TimeoutException)
        {
            var last = await Brain.Get<IView>(desktop + "/view").Read();
            throw new TimeoutException("The desktop showed: " + string.Join(", ", last is null ? [] : Flatten(last.Root).Select(node => node.Key)));
        }
    }

    private static bool Has(UiNode[] nodes, string window) => nodes.Any(node => node.Key == "content-" + window);

    private static IEnumerable<UiNode> Flatten(UiNode root)
    {
        yield return root;
        foreach (var child in root.Children.SelectMany(Flatten)) { yield return child; }
    }
}
