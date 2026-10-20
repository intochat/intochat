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
        await Brain.Get<IApp>(desktop).Configure(new() { ["Layout"] = "desktop" }, []);
        await AppRuntimeReadiness.WaitAsync(Brain, User, ct);
        await Connect(ct);
        var dock = await Until(desktop, nodes => nodes.Any(node => node.Key == "dock-calculator"), ct);
        // Any second installed app beside the calculator.
        var other = dock.First(node => node.Key.StartsWith("dock-", StringComparison.Ordinal) && node.Key is not ("dock-calculator" or "dock-ino")).Key["dock-".Length..];

        await Click(desktop, "dock-calculator", "open:calculator", ct);
        await Click(desktop, "dock-" + other, "open:" + other, ct);
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
        await Brain.Get<IApp>(desktop).Configure(new() { ["Layout"] = "desktop" }, []);
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
        await Brain.Get<IApp>(desktop).Configure(new() { ["Layout"] = "desktop" }, []);
        await AppRuntimeReadiness.WaitAsync(Brain, User, ct);
        await Connect(ct);
        await Click(desktop, "dock-calculator", "open:calculator", ct);

        await Click(desktop, "pin-calculator", "pin:calculator", ct);
        await TestWait.UntilAsync(_ => workspace.Read(), state => state.Pins.Any(view => view.Id == "calculator"), Patience, ct);
        var dock = await Until(desktop, nodes => nodes.Any(node => node.Key == "unpin-calculator"), ct);
        Assert.Equal("dock-ino", dock.First(node => node.Key.StartsWith("dock-", StringComparison.Ordinal)).Key);

        await Click(desktop, "unpin-calculator", "unpin:calculator", ct);
        await TestWait.UntilAsync(_ => workspace.Read(), state => state.Pins.All(view => view.Id != "calculator"), Patience, ct);
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
