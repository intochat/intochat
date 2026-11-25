using DigitalBrain;
using DigitalBrain.Neurons;
using global::Microsoft.Extensions.DependencyInjection;
using IntoChat.OS.Apps.Desktop;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Viewport;
using IntoChat.OS.Modules.Flutter.Workspace;
using IntoChat.OS.Runtime;
using TestOS = DigitalBrain.Testing.TestOS;
using Xunit;

namespace IntoChat.OS.Modules.Flutter;

public sealed class WorkspaceWindowsFacts
{

    [Fact]
    public void WorkspaceWindows_Derive_PlacesUpTo8Windows()
    {
        var views = Enumerable.Range(1, 8)
            .Select(i => new WorkspaceViewRef($"v-{i}", $"owner/apps/app-{i}", "full", ""))
            .ToArray();

        var windows = WorkspaceWindows.Derive(views, "v-1");

        Assert.Equal(8, windows.Length);
        for (var i = 0; i < 8; i++)
        {
            Assert.Single(windows[i].Tabs);
            Assert.Equal($"v-{i + 1}", windows[i].Tabs[0].Id);
            Assert.Equal($"v-{i + 1}", windows[i].ActiveTabId);
        }
    }

    [Fact]
    public void WorkspaceWindows_Derive_Exceeding8Windows_PlacesIntoExistingWindows()
    {
        // 10 views: 8 windows; Place adds overflow views 9 and 10 to the first window with room (w-1 -> 3 tabs).
        var views = Enumerable.Range(1, 10)
            .Select(i => new WorkspaceViewRef($"v-{i}", $"owner/apps/app-{i}", "full", ""))
            .ToArray();

        var windows = WorkspaceWindows.Derive(views, "v-1");

        Assert.Equal(8, windows.Length);
        Assert.Equal(3, windows[0].Tabs.Length);
        Assert.Single(windows[1].Tabs);
        Assert.Equal("v-1", windows[0].ActiveTabId);
    }

    [Fact]
    public void WorkspaceWindows_Reconcile_CapsTabsAt8_EvictsOldestUnfocusedTab()
    {
        // A single window holding 9 tabs, with v-9 focused: caps w-1 at 8 tabs and places evicted v-2 into w-2.
        var tabs = Enumerable.Range(1, 9)
            .Select(i => new WorkspaceViewRef($"v-{i}", $"owner/apps/app-{i}", "full", ""))
            .ToArray();

        var window = new WorkspaceWindow("w-1", tabs, "v-1");
        var reconciled = WorkspaceWindows.Reconcile([window], tabs, "v-9");

        Assert.Equal(2, reconciled.Length);
        Assert.Equal(8, reconciled[0].Tabs.Length);
        Assert.Contains(reconciled[0].Tabs, t => t.Id == "v-9");
        Assert.Equal("v-9", reconciled[0].ActiveTabId);
        Assert.Single(reconciled[1].Tabs);
        Assert.Equal("v-2", reconciled[1].Tabs[0].Id);
    }

    [Fact]
    public void WorkspaceWindows_Reconcile_CapsWindowsAt8_EvictsOldestUnfocusedWindow()
    {
        // 9 windows, each with 1 tab. Window 9 has focused view v-9.
        var views = Enumerable.Range(1, 9)
            .Select(i => new WorkspaceViewRef($"v-{i}", $"owner/apps/app-{i}", "full", ""))
            .ToArray();

        var nineWindows = Enumerable.Range(1, 9)
            .Select(i => new WorkspaceWindow($"w-{i}", [views[i - 1]], $"v-{i}"))
            .ToArray();

        var reconciled = WorkspaceWindows.Reconcile(nineWindows, views, "v-9");

        Assert.Equal(8, reconciled.Length);
        // The window containing focused view v-9 is preserved
        Assert.Contains(reconciled, w => w.Tabs.Any(t => t.Id == "v-9"));
        // Oldest unfocused window w-1 was evicted
        Assert.DoesNotContain(reconciled, w => w.Id == "w-1");
    }

    [Fact]
    public void WorkspaceWindows_Derive_Exceeding64Views_EvictsOldestUnfocusedTab()
    {
        // 65 views with v-50 focused
        var views = Enumerable.Range(1, 65)
            .Select(i => new WorkspaceViewRef($"v-{i}", $"owner/apps/app-{i}", "full", ""))
            .ToArray();

        var windows = WorkspaceWindows.Derive(views, "v-50");

        Assert.Equal(8, windows.Length);
        var totalTabs = windows.Sum(w => w.Tabs.Length);
        Assert.Equal(64, totalTabs);
        // Every window has at most 8 tabs
        Assert.All(windows, w => Assert.InRange(w.Tabs.Length, 1, 8));
        // Focused view v-50 is preserved
        Assert.Contains(windows, w => w.Tabs.Any(t => t.Id == "v-50"));
    }

    [Fact]
    public async Task WorkspaceNeuron_Close_RemovesAppAndMaintainsFocusedViewId()
    {
        using var person = Edge.AsPerson("owner");
        await using var os = TestOS.Local().Register(typeof(FlutterModule).Assembly);

        var ws = os.Get<IWorkspace>(DigitalBrain.Contracts.PlainNeuronContracts.IdOf(typeof(IWorkspace), IWorkspace.Key("owner")));
        var initial = await ws.Read();
        Assert.NotNull(initial);

        // Show calculator
        await ws.Show("owner/apps/calculator", "op-1", initial.Revision);
        var afterShow = await ws.Read();
        Assert.Contains("owner/apps/calculator", afterShow.Open);
        Assert.Contains(afterShow.OpenViews, v => v.App == "owner/apps/calculator");
        Assert.Equal("calculator", afterShow.FocusedViewId);

        // Close calculator
        await ws.Close("owner/apps/calculator", "op-2", afterShow.Revision);
        var afterClose = await ws.Read();
        Assert.DoesNotContain("owner/apps/calculator", afterClose.Open);
        Assert.DoesNotContain(afterClose.OpenViews, v => v.App == "owner/apps/calculator");
        // FocusedViewId should be maintained or null/fallback
        Assert.NotEqual("calculator", afterClose.FocusedViewId);
    }

    [Fact]
    public async Task WorkspaceNeuron_Show_AddsViewAndSetsFocus()
    {
        using var person = Edge.AsPerson("owner");
        await using var os = TestOS.Local().Register(typeof(FlutterModule).Assembly);

        var ws = os.Get<IWorkspace>(DigitalBrain.Contracts.PlainNeuronContracts.IdOf(typeof(IWorkspace), IWorkspace.Key("owner")));
        var initial = await ws.Read();

        var state = await ws.Show("owner/apps/creator", "op-show", initial.Revision);
        Assert.Contains("owner/apps/creator", state.Open);
        Assert.Contains(state.OpenViews, v => v.App == "owner/apps/creator");
        Assert.Equal("creator", state.FocusedViewId);
    }

    [Fact]
    public async Task DesktopApp_UiActivation_AppliesWorkspaceStateToRealWorkspaceNeuron_AndRefusesDirectOrUnshippedCalls()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", new BrainId("owner")));
        using var services = new global::Microsoft.Extensions.DependencyInjection.ServiceCollection()
            .AddOptions<AppsOptions>()
            .Services
            .AddSingleton(TimeProvider.System)
            .AddSingleton(new FeatureCatalog([typeof(AppsModule).Assembly, typeof(DesktopApp).Assembly]))
            .BuildServiceProvider();
        await using var os = TestOS.Local(options => options.Services = services);
        os.Register<DesktopRecordsNeuron>();
        os.RegisterCatalog();
        os.Register(typeof(FlutterModule).Assembly);
        os.Register(typeof(DesktopApp).Assembly);

        var brain = new BrainId("owner");
        var desktopAppRef = IntoChat.OS.Host.Primitives.AppRef.Of(brain, "desktop");
        var desktopAppNeuron = os.Get<IApp>(DigitalBrain.Contracts.PlainNeuronContracts.IdOf(typeof(IApp), desktopAppRef.Key));
        await desktopAppNeuron.Import(
            DesktopApp.Bundle,
            new AppOrigin(ShippedApps.Publisher, new AppSource.Shipped(), "desktop", DesktopApp.Bundle.Version));
        await os.Get<IApp>(DigitalBrain.Contracts.PlainNeuronContracts.IdOf(typeof(IApp), IntoChat.OS.Host.Primitives.AppRef.Of(brain, "files").Key))
            .Import(
                IntoChat.OS.Modules.Apps.FilesWorkspaceScreen.Bundle,
                new AppOrigin(ShippedApps.Publisher, new AppSource.Shipped(), "files", IntoChat.OS.Modules.Apps.FilesWorkspaceScreen.Bundle.Version));
        await os.Get<IApp>(DigitalBrain.Contracts.PlainNeuronContracts.IdOf(typeof(IApp), IntoChat.OS.Host.Primitives.AppRef.Of(brain, "settings").Key))
            .Import(
                IntoChat.OS.Apps.Settings.SettingsApp.Bundle,
                new AppOrigin(ShippedApps.Publisher, new AppSource.Shipped(), "settings", IntoChat.OS.Apps.Settings.SettingsApp.Bundle.Version));

        var desktopScreen = os.Get<IScreen>(desktopAppRef.ScreenNeuron);
        var published = await desktopScreen.Read();
        Assert.NotNull(published);

        var ws = os.Get<IWorkspace>(DigitalBrain.Contracts.PlainNeuronContracts.IdOf(typeof(IWorkspace), IWorkspace.Key("owner")));

        using (os.Edge.Enter(os.Edge.Person("alice", "acct", brain)))
        {
            await desktopScreen.Activate("app.desktop", published!.Generation, "dock-files", by: "alice");
            await os.Idle();

            var updated = await ws.Read();
            Assert.Contains(updated.OpenViews, v => v.App == IntoChat.OS.Host.Primitives.AppRef.Of(brain, "files").Key);
            Assert.Equal("files", updated.FocusedViewId);

            var filesWin = Assert.Single(updated.Windows, w => w.ActiveTabId == "files");
            var vp = os.Get<IViewport>(desktopAppRef.ChildNeuron(UIVocabulary.ViewportType, $"window/{filesWin.Id}"));
            var vpState = await vp.Read();
            Assert.NotNull(vpState.Target);
            Assert.Equal(IntoChat.OS.Host.Primitives.AppRef.Of(brain, "files").Key, vpState.Target!.App);

            // Direct person call to IWorkspace.Apply is still refused.
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                ws.Apply(new WorkspaceUpdate([], null, []), "direct-person", updated.Revision));
        }

        // Foreign app caller is still refused for both WorkspaceNeuron and ViewportNeuron.
        var foreignKey = IntoChat.OS.Host.Primitives.AppRef.Of(brain, "other").Key;
        using (os.Edge.Enter(os.Edge.App(foreignKey, new DigitalBrain.Access.Actor.Person("alice", "acct"), brain)))
        {
            var current = await ws.Read();
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                ws.Apply(new WorkspaceUpdate([], null, []), "foreign-app", current.Revision));
            var win = Assert.Single(current.Windows, w => w.ActiveTabId == "files");
            var vp = os.Get<IViewport>(desktopAppRef.ChildNeuron(UIVocabulary.ViewportType, $"window/{win.Id}"));
            var vpState = await vp.Read();
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                vp.Set(new WorkspaceViewRef("hijack", foreignKey, "full", ""), vpState.Revision));
        }

        // Edited (non-shipped) desktop app is refused even through DesktopApp.
        await desktopAppNeuron.SaveManifest(DesktopApp.Bundle.Manifest, Guid.NewGuid());
        var latestDesktopView = await desktopScreen.Read();
        Assert.NotNull(latestDesktopView);
        using (os.Edge.Enter(os.Edge.Person("alice", "acct", brain)))
        {
            await desktopScreen.Activate("app.desktop", latestDesktopView!.Generation, "dock-settings", by: "alice");
            await os.Idle();
            var afterEditedActivate = await ws.Read();
            Assert.DoesNotContain(afterEditedActivate.OpenViews, v => v.App == IntoChat.OS.Host.Primitives.AppRef.Of(brain, "settings").Key);
        }

        using (os.Edge.Enter(os.Edge.App(desktopAppRef.Key, new DigitalBrain.Access.Actor.Person("alice", "acct"), brain)))
        {
            var current = await ws.Read();
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                ws.Apply(new WorkspaceUpdate([], null, []), "edited-desktop", current.Revision));
        }
    }
}
