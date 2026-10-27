using System.Net.Http.Json;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Chat;
using IntoChat.OS.Modules.Flutter.Views;
using IntoChat.OS.Modules.Flutter.Workspace;

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
        await Brain.Get<IntoChat.OS.Modules.Flutter.Text.IText>(desktop + "/layout").Set("desktop");
        await Brain.Get<IApp>(desktop).Configure(new(), []);
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

    [Fact(Timeout = 360_000)]
    public async Task TheFirstStartOpensInoWithOneGreetingAndASecondStartDoesNotGreetAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        using var caller = User.AsCaller();
        var desktop = WorkspaceId + "/apps/desktop";
        var transcript = Brain.Get<IChat>(WorkspaceId + "/apps/ino/transcript");
        await Brain.Get<IntoChat.OS.Modules.Flutter.Text.IText>(desktop + "/layout").Set("desktop");
        await Brain.Get<IApp>(desktop).Configure(new(), []);
        await AppRuntimeReadiness.WaitAsync(Brain, User, ct);
        await Connect(ct);

        await TestWait.UntilAsync(_ => Brain.Get<IWorkspace>(IWorkspace.Key(WorkspaceId)).Read(),
            state => state.OpenViews.Any(view => view.Id == "ino") && state.FocusedViewId == "ino", TimeSpan.FromSeconds(90), ct);
        var greeted = await TestWait.UntilAsync(_ => transcript.Read(),
            chat => chat.Projection?.Messages.Length > 0, TimeSpan.FromSeconds(90), ct);
        var greeting = Assert.Single(greeted.Projection!.Messages);
        Assert.Equal(ChatRole.Assistant, greeting.Role);
        Assert.Contains("apps", greeting.Text, StringComparison.OrdinalIgnoreCase);
        // Composing Ino back-fills the selected conversation into the chat index; the greeting is not a user message.
        var indexed = await TestWait.UntilAsync(_ => Brain.Get<IChatIndex>(WorkspaceId + "/apps/ino/chats").Get("main"),
            entry => entry is not null, TimeSpan.FromSeconds(90), ct);
        Assert.Equal(ChatIndex.Untitled, indexed!.Title);

        // Second start: a new program generation re-runs start.cs; the durable flag keeps it to one greeting.
        // Ino's greeting can land before start.cs publishes the desktop: wait for the first view.
        var before = (await TestWait.UntilAsync(_ => Brain.Get<IView>(desktop + "/view").Read(), view => view is not null, TimeSpan.FromSeconds(90), ct))!.Generation;
        await Brain.Get<IntoChat.OS.Modules.Flutter.Text.IText>(desktop + "/layout").Set("desktop");
        await Brain.Get<IApp>(desktop).Configure(new(), []);
        // Not AppRuntimeReadiness here: it never settles after Ino's greeting, on master too (b784ae493). Inspect shows
        // apps/memory/capture.cs holding one undelivered TurnCompleted from apps/memory/agents/capture, while every
        // other behavior is Running with nothing pending. The new view generation below is the desktop's own readiness.
        await Connect(ct);
        await TestWait.UntilAsync(_ => Brain.Get<IView>(desktop + "/view").Read(), view => view!.Generation > before, TimeSpan.FromSeconds(90), ct);
        await Task.Delay(TimeSpan.FromSeconds(5), ct);
        Assert.Single((await transcript.Read()).Projection!.Messages);
        Assert.Equal("true", (await Brain.Get<IntoChat.OS.Modules.Flutter.Text.IText>(desktop + "/greeted").Read()).Markdown);
    }

    private async Task Connect(CancellationToken ct)
    {
        using var connected = await User.Http.PostAsJsonAsync($"/brains/{WorkspaceId}/apps/connect", new { }, ct);
        connected.EnsureSuccessStatusCode();
    }

    private static IEnumerable<UiNode> Flatten(UiNode root)
    {
        yield return root;
        foreach (var child in root.Children.SelectMany(Flatten)) { yield return child; }
    }
}
