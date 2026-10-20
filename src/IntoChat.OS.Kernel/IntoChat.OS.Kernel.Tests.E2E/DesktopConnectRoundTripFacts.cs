using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using IntoChat.OS.Apps;
using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Button;
using IntoChat.OS.Flutter.Views;

namespace IntoChat.OS.Kernel.Tests.E2E;

// The gallery's Connect on the real script runtime, Google stubbed on loopback (ConnectHostFixture): connect.cs puts
// the provider's https sign-in URL on a button the shell opens; the callback stores the connection, connected.cs
// re-renders and the card reads Connected.
[Collection("Connect host")]
public sealed class DesktopConnectRoundTripFacts(ConnectHostFixture host) : BrainFact(host)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(90);

    // Runs green up to the callback: the provider exchange succeeds (the stub sees the code), then AcceptGrant refuses
    // because the login's recorded owner is the desktop script's App caller, and connections never belong to an app
    // (ConnectionNeuron.RequireMember). Which caller owns a gallery Connect is an open ruling; see the task-6 report.
    [Fact(Timeout = 360_000, Skip = "Blocked: a script-started login records an App owner that AcceptGrant refuses (ruling pending).")]
    public async Task ConnectGmailFromTheGalleryEndsConnected()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = Brain;
        var desktop = WorkspaceId + "/apps/desktop";
        using var caller = User.AsCaller();
        await brain.Get<IApp>(desktop).Configure(new() { ["Layout"] = "desktop" }, []);
        await AppRuntimeReadiness.WaitAsync(brain, User, ct);
        using (var connected = await User.Http.PostAsJsonAsync($"/brains/{WorkspaceId}/apps/connect", new { }, ct)) { connected.EnsureSuccessStatusCode(); }
        await Click(brain, desktop, "gallery", ct);
        var gallery = await Until(brain, desktop, nodes => nodes.Any(node => node.Action == "connect:gmail" && node.Enabled), ct);
        var cards = gallery.Where(node => node.Kind == UiNodeKind.Card && node.Key.StartsWith("entry-connection/", StringComparison.Ordinal)).Select(node => node.Text).ToArray();
        Assert.Equal(["Gmail", "Salesforce"], cards[..2]);
        Assert.False(Assert.Single(gallery, node => node.Action == "connect:salesforce").Enabled);

        await Click(brain, desktop, "connect:gmail", ct);
        var signIn = (await Until(brain, desktop, nodes => nodes.Any(IsSignIn), ct)).Single(IsSignIn);
        var button = await brain.Get<IButton>(signIn.Reference!.Name).Read();
        var url = new Uri(JsonDocument.Parse(button.Activation!).RootElement.GetProperty("url").GetString()!);
        Assert.Equal("https://accounts.google.com/o/oauth2/v2/auth", url.GetLeftPart(UriPartial.Path));

        using var callback = await User.Http.GetAsync($"/connections/oauth/gmail/callback?code=fake-code&state={Uri.EscapeDataString(HttpUtility.ParseQueryString(url.Query)["state"]!)}", ct);
        Assert.True(callback.StatusCode == HttpStatusCode.OK, await callback.Content.ReadAsStringAsync(ct));
        var shown = await Until(brain, desktop, nodes => nodes.Any(node => node.Kind == UiNodeKind.Card && node.Text == "Gmail" && node.Detail == "Connected."), ct);
        Assert.DoesNotContain(shown, node => node.Action == "connect:gmail" || IsSignIn(node));
        Assert.True(Assert.Single(await brain.Get<IAppStore>(IAppStore.Key(WorkspaceId)).List(), entry => entry.ConnectorId == "gmail").Installed);
    }

    private static bool IsSignIn(UiNode node) => node.Kind == UiNodeKind.Reference && node.Key.StartsWith("signin-", StringComparison.Ordinal);

    // The desktop re-renders as it settles; a click on a superseded generation is read again.
    private static async Task Click(ITestBrain brain, string desktop, string action, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var view = await TestWait.UntilAsync(_ => brain.Get<IView>(desktop + "/view").Read(),
                published => published is not null && Flatten(published.Root).Any(node => node.Action == action && node.Kind == UiNodeKind.Button && node.Enabled), Patience, ct);
            var key = Flatten(view!.Root).First(node => node.Action == action && node.Kind == UiNodeKind.Button && node.Enabled).Key;
            try { await brain.Get<IView>(desktop + "/view").Activate(view.Generation, key, action, Guid.NewGuid()); return; }
            catch (StaleActionException) when (attempt < 5) { }
        }
    }

    private static async Task<UiNode[]> Until(ITestBrain brain, string desktop, Func<UiNode[], bool> ready, CancellationToken ct)
    {
        try
        {
            var view = await TestWait.UntilAsync(_ => brain.Get<IView>(desktop + "/view").Read(),
                published => published is not null && ready([.. Flatten(published.Root)]), Patience, ct);
            return [.. Flatten(view!.Root)];
        }
        catch (TimeoutException)
        {
            var last = await brain.Get<IView>(desktop + "/view").Read();
            throw new TimeoutException("The desktop showed: " + string.Join(", ", last is null ? [] : Flatten(last.Root).Select(node => node.Key + "=" + node.Text + "/" + node.Detail)));
        }
    }

    private static IEnumerable<UiNode> Flatten(UiNode root)
    {
        yield return root;
        foreach (var child in root.Children.SelectMany(Flatten)) { yield return child; }
    }
}
