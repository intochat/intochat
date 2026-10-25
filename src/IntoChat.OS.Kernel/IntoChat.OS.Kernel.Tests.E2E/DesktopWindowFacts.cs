using System.Net.Http.Json;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Viewport;
using IntoChat.OS.Modules.Flutter.Views;
using IntoChat.OS.Modules.Flutter.Workspace;

namespace IntoChat.OS.Kernel.Tests.E2E;

// On the real script runtime: the desktop's open/focus/close synapses (windows w-<n>, tabs by index) change the durable
// workspace, restore.cs re-renders, and a restarted desktop brings the same windows back. Apps other than the dock's
// open as the Apps window's Open does: a workspace activation the root's receiver (the Workspace app) applies.
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
        await Brain.Get<IntoChat.OS.Modules.Flutter.Text.IText>(desktop + "/layout").Set("desktop");
        await Brain.Get<IApp>(desktop).Configure(new(), []);
        await AppRuntimeReadiness.WaitAsync(Brain, User, ct);
        await Connect(ct);
        // The dock is narrow (Ino, pins, open apps): the calculator opens as the Apps window's Open does, through the
        // root's activation receiver; the second window is the Apps app itself, from the dock's Apps button.
        var other = "apps";
        await OpenApp("calculator", ct);
        await Click(desktop, "apps-open", "open:apps", ct);
        // Each app opens in its own single-tab window w-<n>; the tab id is the app's name.
        var opened = await TestWait.UntilAsync(_ => workspace.Read(), state => WindowOf(state, "calculator") is not null && WindowOf(state, other) is not null, Patience, ct);
        var (calculator, second) = (WindowOf(opened, "calculator")!, WindowOf(opened, other)!);
        Assert.NotEqual(calculator, second);
        Assert.All([calculator, second], id => Assert.StartsWith("w-", id, StringComparison.Ordinal));
        var both = await Until(desktop, nodes => Has(nodes, calculator) && Has(nodes, second), ct);
        Assert.Equal([UIVocabulary.ViewportType], both.Where(node => node.Key == "content-" + calculator || node.Key == "content-" + second).Select(node => node.Reference!.Kind).Distinct());
        var target = await TestWait.UntilAsync(_ => Brain.Get<IViewport>(desktop + "/window/" + calculator).Read(),
            state => state.Target is not null, Patience, ct);
        Assert.Equal(WorkspaceId + "/apps/calculator", target.Target!.App);
        var shown = await TestWait.UntilAsync(_ => Brain.Get<IViewport>(desktop + "/window/" + second).Read(),
            state => state.Target is not null, Patience, ct);
        Assert.Equal(WorkspaceId + "/apps/" + other, shown.Target!.App);
        Assert.Equal(other, (await workspace.Read()).FocusedViewId);

        await Click(desktop, "focus-" + calculator, "focus:" + calculator, ct);
        await TestWait.UntilAsync(_ => workspace.Read(), state => state.FocusedViewId == "calculator", Patience, ct);

        // The window's only tab: closing it closes the window and clears its viewport.
        await Click(desktop, "close-" + second, "close:" + second + "/0", ct);
        await Until(desktop, nodes => Has(nodes, calculator) && !Has(nodes, second), ct);
        await TestWait.UntilAsync(_ => Brain.Get<IViewport>(desktop + "/window/" + second).Read(), state => state.Target is null, Patience, ct);
        Assert.Null(WindowOf(await workspace.Read(), other));

        // Restart: a new program generation re-runs start.cs, which reads the durable workspace.
        var before = (await Brain.Get<IView>(desktop + "/view").Read())!.Generation;
        await Brain.Get<IntoChat.OS.Modules.Flutter.Text.IText>(desktop + "/layout").Set("desktop");
        await Brain.Get<IApp>(desktop).Configure(new(), []);
        // Not AppRuntimeReadiness here: it never settles after Ino's greeting, on master too (b784ae493). Inspect shows
        // apps/memory/capture.cs holding one undelivered TurnCompleted from apps/memory/agents/capture, while every
        // other behavior is Running with nothing pending. The new view generation below is the desktop's own readiness.
        await Connect(ct);
        var restored = await TestWait.UntilAsync(_ => Brain.Get<IView>(desktop + "/view").Read(),
            view => view!.Generation > before && Has([.. Flatten(view.Root)], calculator), Patience, ct);
        Assert.DoesNotContain(Flatten(restored!.Root), node => node.Key == "content-" + second);
        var after = await workspace.Read();
        Assert.Equal("calculator", after.FocusedViewId);
        Assert.Equal(calculator, WindowOf(after, "calculator"));
    }

    [Fact(Timeout = 360_000)]
    public async Task PinningKeepsAnAppInTheWorkspacePinsAndUnpinningRemovesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        using var caller = User.AsCaller();
        var desktop = WorkspaceId + "/apps/desktop";
        var workspace = Brain.Get<IWorkspace>(WorkspaceId);
        await Brain.Get<IntoChat.OS.Modules.Flutter.Text.IText>(desktop + "/layout").Set("desktop");
        await Brain.Get<IApp>(desktop).Configure(new(), []);
        await AppRuntimeReadiness.WaitAsync(Brain, User, ct);
        await Connect(ct);
        await OpenApp("calculator", ct);

        var window = WindowOf(await TestWait.UntilAsync(_ => workspace.Read(), state => WindowOf(state, "calculator") is not null, Patience, ct), "calculator")!;

        await Click(desktop, "pin-" + window, "pin:calculator", ct);
        await TestWait.UntilAsync(_ => workspace.Read(), state => state.Pins.Any(view => view.Id == "calculator"), Patience, ct);
        var dock = await Until(desktop, nodes => nodes.Any(node => node.Key == "unpin-" + window), ct);
        Assert.Equal("dock-ino", dock.First(node => node.Key.StartsWith("dock-", StringComparison.Ordinal)).Key);

        await Click(desktop, "unpin-" + window, "unpin:calculator", ct);
        await TestWait.UntilAsync(_ => workspace.Read(), state => state.Pins.All(view => view.Id != "calculator"), Patience, ct);
    }

    // The Apps window's Install is the person's own install request; its result opens the app in a window.
    [Fact(Timeout = 360_000)]
    public async Task AnotherAccountsAppInstalledAsThePersonOpensInAWindow()
    {
        var ct = TestContext.Current.CancellationToken;
        var desktop = WorkspaceId + "/apps/desktop";
        // Another account publishes an app this user does not have.
        var name = "echo" + Guid.NewGuid().ToString("N")[..8];
        using (var author = await Brain.PersonAsync("author-" + Guid.NewGuid().ToString("N"), ct))
        using (author.AsCaller())
        {
            var app = Brain.Get<IApp>(IApp.Key(author.BrainId, name));
            await app.Create(new("Echo " + name, "echoes", [new("ask", "ask it")], [], []), "## Scenario: Echoes\nIt echoes.");
            await app.BeginBuild(null);
            await app.CompleteBuild(await app.CurrentBuildAttempt(), new() { ["synapses/echo.cs"] = Echo }, null);
            await app.Publish();
        }
        using var caller = User.AsCaller();
        var publisher = (await TestWait.UntilAsync(_ => Brain.Get<IAppStore>(IAppStore.Key(WorkspaceId)).List(),
            entries => entries.Any(entry => entry.Name == name), Patience, ct)).First(entry => entry.Name == name).Publisher;
        await Brain.Get<IntoChat.OS.Modules.Flutter.Text.IText>(desktop + "/layout").Set("desktop");
        await Brain.Get<IApp>(desktop).Configure(new(), []);
        await AppRuntimeReadiness.WaitAsync(Brain, User, ct);
        await Connect(ct);

        using (var installed = await User.Http.PostAsJsonAsync($"/brains/{WorkspaceId}/apps/install",
            new { @ref = new { publisher, app = name }, consent = AppConsent.Of(null) }, ct))
        {
            installed.EnsureSuccessStatusCode();
            Assert.Equal(name, (await installed.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(ct)).GetProperty("installedName").GetString());
        }
        await OpenApp(name, ct);
        var placed = WindowOf(await TestWait.UntilAsync(_ => Brain.Get<IWorkspace>(WorkspaceId).Read(), state => WindowOf(state, name) is not null, Patience, ct), name)!;
        var window = await TestWait.UntilAsync(_ => Brain.Get<IViewport>(desktop + "/window/" + placed).Read(), state => state.Target is not null, Patience, ct);
        Assert.Equal(WorkspaceId + "/apps/" + name, window.Target!.App);
        Assert.Equal(name, (await Brain.Get<IWorkspace>(WorkspaceId).Read()).FocusedViewId);
        Assert.DoesNotContain(await Until(desktop, nodes => Has(nodes, placed), ct), node => node.Key.StartsWith("gallery", StringComparison.Ordinal));
    }

    // More's Settings carries a workspace activation; the root's receiver opens the Settings window on its tab.
    [Fact(Timeout = 360_000)]
    public async Task MoreSettingsOpensTheSettingsWindow()
    {
        var ct = TestContext.Current.CancellationToken;
        using var caller = User.AsCaller();
        var desktop = WorkspaceId + "/apps/desktop";
        await Brain.Get<IntoChat.OS.Modules.Flutter.Text.IText>(desktop + "/layout").Set("desktop");
        await Brain.Get<IApp>(desktop).Configure(new(), []);
        await AppRuntimeReadiness.WaitAsync(Brain, User, ct);
        await Connect(ct);
        var settings = await TestWait.UntilAsync(_ => Brain.Get<IntoChat.OS.Modules.Flutter.Button.IButton>(desktop + "/settings").Read(),
            button => button.Activation?.Contains("\"workspace\"", StringComparison.Ordinal) == true, Patience, ct);
        var activation = System.Text.Json.JsonDocument.Parse(settings.Activation!).RootElement;
        Assert.Equal(("workspace", WorkspaceId, "settings", "open", "general"), (activation.GetProperty("kind").GetString(), activation.GetProperty("brainId").GetString(),
            activation.GetProperty("application").GetString(), activation.GetProperty("operation").GetString(), activation.GetProperty("input").GetString()));
        await Activate(settings.Activation!, ct);
        var placed = WindowOf(await TestWait.UntilAsync(_ => Brain.Get<IWorkspace>(WorkspaceId).Read(), state => WindowOf(state, "settings") is not null, Patience, ct), "settings")!;
        var window = await TestWait.UntilAsync(_ => Brain.Get<IViewport>(desktop + "/window/" + placed).Read(), state => state.Target is not null, Patience, ct);
        Assert.Equal(WorkspaceId + "/apps/settings", window.Target!.App);
        // Again: the window already showing Settings is focused, not duplicated.
        await Activate(settings.Activation!, ct);
        await Task.Delay(TimeSpan.FromSeconds(1), ct);
        Assert.Single((await Brain.Get<IWorkspace>(WorkspaceId).Read()).Windows, candidate => candidate.Tabs.Any(view => view.Id == "settings"));
    }

    // A minimal real synapse: it answers "ask" requests by doing nothing.
    private const string Echo = """
        using IntoChat.OS.Modules.Apps;
        using IntoChat.OS.Client;
        await using var brain = await IntoChatOSClient.ConnectAsync(args);
        await foreach (var request in brain.On(brain.Get<IApp>(brain.Setting("App")!), "ask", brain.Stopping)) { }
        """;

    // What the shell does with a workspace activation on the desktop root: hand it to the root's receiver, Workspace.
    private async Task Activate(string activation, CancellationToken ct)
    {
        using var response = await User.Http.PostAsJsonAsync($"/brains/{WorkspaceId}/apps/workspace/request", new { operation = "activate", input = activation }, ct);
        response.EnsureSuccessStatusCode();
    }

    // The receiver subscribes to requests from its start: one sent while a connect's update restarts it is not seen,
    // so (as a person clicks again) the activation is sent again until the app has a window.
    private async Task OpenApp(string name, CancellationToken ct)
    {
        var activation = System.Text.Json.JsonSerializer.Serialize(new { kind = "workspace", brainId = WorkspaceId, application = name });
        var workspace = Brain.Get<IWorkspace>(WorkspaceId);
        for (var attempt = 0; attempt < 12; attempt++)
        {
            await Activate(activation, ct);
            try { await TestWait.UntilAsync(_ => workspace.Read(), state => WindowOf(state, name) is not null, TimeSpan.FromSeconds(10), ct); return; }
            catch (TimeoutException) { }
        }
        throw new TimeoutException("No window opened for " + name + ": " + (await Brain.Get<IntoChat.OS.Modules.Flutter.Text.IText>(WorkspaceId + "/apps/workspace/navigation-error").Read()).Markdown);
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

    // The id of the window holding the tab, or null.
    private static string? WindowOf(WorkspaceState state, string tab) => state.Windows.FirstOrDefault(window => window.Tabs.Any(view => view.Id == tab))?.Id;

    private static IEnumerable<UiNode> Flatten(UiNode root)
    {
        yield return root;
        foreach (var child in root.Children.SelectMany(Flatten)) { yield return child; }
    }
}
