using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using IntoChat.OS.Apps;
using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Button;
using IntoChat.OS.Flutter.Views;

namespace IntoChat.OS.Kernel.Tests.E2E;

// The gallery's Connect on the real script runtime, Google stubbed on loopback (ConnectHostFixture). The Connect
// button's activation hands the shell its connection window for Gmail; that window's login is the user's own
// HTTP call, made here the way ConnectWindow makes it (its widget test covers the click). The callback stores the
// connection. Manual: the browser at Google, and (see below) seeing the card turn Connected on this host.
[Collection("Connect host")]
public sealed class DesktopConnectRoundTripFacts(ConnectHostFixture host) : BrainFact(host)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(90);

    [Fact(Timeout = 360_000)]
    public async Task ConnectGmailFromTheGalleryReachesTheProviderAsTheUser()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = Brain;
        var desktop = WorkspaceId + "/apps/desktop";
        using var caller = User.AsCaller();
        await brain.Get<IApp>(desktop).Configure(new() { ["Layout"] = "desktop" }, []);
        await AppRuntimeReadiness.WaitAsync(brain, User, ct);
        using (var connected = await User.Http.PostAsJsonAsync($"/brains/{WorkspaceId}/apps/connect", new { }, ct)) { connected.EnsureSuccessStatusCode(); }
        await Click(brain, desktop, "gallery", ct);
        var gallery = await Until(brain, desktop, nodes => nodes.Any(node => node.Key == "connect-connection/gmail" && node.Kind == UiNodeKind.Reference), ct);
        var cards = gallery.Where(node => node.Kind == UiNodeKind.Card && node.Key.StartsWith("entry-connection/", StringComparison.Ordinal)).Select(node => node.Text).ToArray();
        Assert.Equal(["Gmail", "Salesforce"], cards[..2]);
        Assert.False(Assert.Single(gallery, node => node.Key == "connect-connection/salesforce").Enabled);

        // The Connect button opens the shell's connection window for Gmail in this brain; no URL, no script login.
        var connect = Assert.Single(gallery, node => node.Key == "connect-connection/gmail");
        var button = await TestWait.UntilAsync(_ => brain.Get<IButton>(connect.Reference!.Name).Read(), state => state.Activation is not null, Patience, ct);
        var activation = JsonDocument.Parse(button.Activation!).RootElement;
        Assert.Equal(("connections", WorkspaceId, "gmail"), (activation.GetProperty("kind").GetString(), activation.GetProperty("brainId").GetString(), activation.GetProperty("connector").GetString()));

        // What ConnectWindow posts for that connector, as the signed-in user.
        using var login = await User.Http.PostAsJsonAsync($"/brains/{WorkspaceId}/connections/gmail/login", new { connectorId = "gmail", authMethodId = "google-oauth" }, ct);
        login.EnsureSuccessStatusCode();
        var url = new Uri((await login.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("url").GetString()!);
        Assert.Equal("https://accounts.google.com/o/oauth2/v2/auth", url.GetLeftPart(UriPartial.Path));
        var exchanges = host.Tokens.Forms.Count;
        using var callback = await User.Http.GetAsync($"/connections/oauth/gmail/callback?code=fake-code&state={Uri.EscapeDataString(HttpUtility.ParseQueryString(url.Query)["state"]!)}", ct);
        // The callback claimed the user's login and exchanged the code at the provider (the stub saw it). On this
        // Aspire host Gmail's mailbox check after the exchange never reaches the stub's /profile (GmailUnavailableException),
        // so "Connected" on the card is not observable here; ListOffers' Connected after a callback is GalleryConnectFacts.
        Assert.Equal(exchanges + 1, host.Tokens.Forms.Count);
        Assert.Equal("fake-code", host.Tokens.Forms[^1]["code"]);
    }

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
