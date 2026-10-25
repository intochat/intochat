using System.Net.Http.Json;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Views;

namespace IntoChat.OS.Kernel.Tests.E2E;

// On the real script runtime and the reference host, which configures no Google or Salesforce OAuth
// client: the dock's Apps opens the Apps window, whose connections show both as not available, and nothing
// breaks. The configured round trip (sign-in URL, callback, Connected) is GalleryConnectFacts (Authority) plus
// DesktopConnectRoundTripFacts; this host has no OAuth provider stub to complete it against.
[Collection("Reference host")]
public sealed class DesktopConnectFacts(ReferenceBrainFixture host) : BrainFact(host)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(90);

    [Fact(Timeout = 360_000)]
    public async Task GmailAndSalesforceShowAsNotAvailableWithoutAnOAuthClient()
    {
        var ct = TestContext.Current.CancellationToken;
        using var caller = User.AsCaller();
        var desktop = WorkspaceId + "/apps/desktop";
        await Brain.Get<IntoChat.OS.Modules.Flutter.Text.IText>(desktop + "/layout").Set("desktop");
        await Brain.Get<IApp>(desktop).Configure(new(), []);
        await AppRuntimeReadiness.WaitAsync(Brain, User, ct);
        using (var connected = await User.Http.PostAsJsonAsync($"/brains/{WorkspaceId}/apps/connect", new { }, ct)) { connected.EnsureSuccessStatusCode(); }
        await Click(desktop, "open:apps", ct);
        var apps = WorkspaceId + "/apps/apps";
        var library = await Until(apps, nodes => nodes.Any(node => node.Action == "connect:gmail") && nodes.Any(node => node.Action == "connect:salesforce"), ct);
        var others = Flatten(Assert.Single(library, node => node.Key == "connections")).ToArray();
        foreach (var (connector, title) in new[] { ("gmail", "Gmail"), ("salesforce", "Salesforce") })
        {
            Assert.Contains(others, node => node.Kind == UiNodeKind.Card && node.Text == title && node.Detail == "Not available on this host.");
            var button = Assert.Single(others, node => node.Action == "connect:" + connector);
            Assert.Equal(("Not available", false), (button.Text, button.Enabled));
        }
        Assert.DoesNotContain(await Until(desktop, _ => true, ct), node => node.Key.StartsWith("gallery", StringComparison.Ordinal));
    }

    // The desktop re-renders as it settles (start, restore); a click on a superseded generation is read again.
    private async Task Click(string desktop, string action, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { await ClickOnce(desktop, action, ct); return; }
            catch (StaleActionException) when (attempt < 5) { }
        }
    }

    private async Task ClickOnce(string desktop, string action, CancellationToken ct)
    {
        var view = await TestWait.UntilAsync(_ => Brain.Get<IView>(desktop + "/view").Read(),
            published => published is not null && Flatten(published.Root).Any(node => node.Action == action && node.Kind == UiNodeKind.Button), Patience, ct);
        var key = Flatten(view!.Root).First(node => node.Action == action && node.Kind == UiNodeKind.Button).Key;
        await Brain.Get<IView>(desktop + "/view").Activate(view.Generation, key, action, Guid.NewGuid());
    }

    private async Task<UiNode[]> Until(string app, Func<UiNode[], bool> ready, CancellationToken ct)
    {
        try
        {
            var view = await TestWait.UntilAsync(_ => Brain.Get<IView>(app + "/view").Read(),
                published => published is not null && ready([.. Flatten(published.Root)]), Patience, ct);
            return [.. Flatten(view!.Root)];
        }
        catch (TimeoutException)
        {
            var last = await Brain.Get<IView>(app + "/view").Read();
            throw new TimeoutException(app + " showed: " + string.Join(", ", last is null ? [] : Flatten(last.Root).Select(node => node.Key + "=" + node.Text + "/" + node.Detail)));
        }
    }

    private static IEnumerable<UiNode> Flatten(UiNode root)
    {
        yield return root;
        foreach (var child in root.Children.SelectMany(Flatten)) { yield return child; }
    }
}
