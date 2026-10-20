using System.Net.Http.Json;
using IntoChat.OS.Apps;
using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Views;

namespace IntoChat.OS.Kernel.Tests.E2E;

// On the real script runtime (csharp-sandbox): connect sends the desktop its one `start`,
// and start.cs presents the desktop view and publishes a tree with the dock row.
[Collection("Reference host")]
public sealed class DesktopStartFacts(ReferenceBrainFixture host) : BrainFact(host)
{
    [Fact(Timeout = 240_000)]
    public async Task ConnectingRunsStartWhichPublishesTheDesktopWithItsDock()
    {
        var ct = TestContext.Current.CancellationToken;
        using var caller = User.AsCaller();
        var desktop = WorkspaceId + "/apps/desktop";
        await Brain.Get<IApp>(desktop).Configure(new() { ["Layout"] = "desktop" }, []);
        await AppRuntimeReadiness.WaitAsync(Brain, User, ct);

        using var connected = await User.Http.PostAsJsonAsync($"/brains/{WorkspaceId}/apps/connect", new { }, ct);
        connected.EnsureSuccessStatusCode();

        var presented = await TestWait.UntilAsync(_ => Brain.Get<IApp>(desktop).Read(),
            snapshot => snapshot.Presentation?.Root?.Main is not null, TimeSpan.FromSeconds(90), ct);
        Assert.Equal(new UiChildRef(UIVocabulary.ViewType, desktop + "/view"), presented.Presentation!.Root!.Main);
        var view = await TestWait.UntilAsync(_ => Brain.Get<IView>(desktop + "/view").Read(),
            published => published is not null && Flatten(published.Root).Any(node => node.Key == "dock" && node.Kind == UiNodeKind.Row),
            TimeSpan.FromSeconds(90), ct);
        Assert.Contains(Flatten(view!.Root), node => node.Key == "dock-ino");
    }

    private static IEnumerable<UiNode> Flatten(UiNode root)
    {
        yield return root;
        foreach (var child in root.Children.SelectMany(Flatten)) { yield return child; }
    }
}
