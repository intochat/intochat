using System.Diagnostics;
using System.Text.Json;
using IntoChat.OS.Apps;
using IntoChat.OS.Circles;
using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Text;
using IntoChat.OS.Flutter.Views;
using IntoChat.OS.Flutter.Workspace;
using Microsoft.Playwright;

namespace IntoChat.OS.Kernel.Tests.E2E;

// One account and browser context per class, on the collection's already running host.
// Only the roots (desktop, workspace) survive between cases; subjects and support apps are removed.
// Apps open as desktop windows; the service apps the desktop never windows use the classic Workspace.
public sealed class ShippedAppsFixture(ReferenceBrainFixture host) : IAsyncLifetime
{
    private ITestBrain? _brain;
    private BrowserSession? _browser;
    private Person? _person;
    private Exception? _unusable;
    private string? _circle;
    private string? _ownedCircle;
    // The layout the page last mounted per scope: switching stores the desktop's layout text and reloads.
    private readonly Dictionary<string, string> _layouts = new(StringComparer.Ordinal);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);
    // ShippedApps.Services (Desktop.Hidden) without the desktop and workspace: background services the desktop
    // shows in no window; v1 still shows them.
    public static readonly HashSet<string> ClassicOnly = new(["router", "selfimprove", "circleroom", "usage"], StringComparer.Ordinal);
    public string Scope => _circle ?? Person.BrainId;
    public void EnsureUsable() => Assert.True(_unusable is null, $"The shared app fixture could not retire its previous case: {_unusable}");
    public void Poison(Exception error) => _unusable = error;
    public ITestBrain Brain => _brain ?? throw new InvalidOperationException("The fixture has not started.");
    public Person Person => _person ?? throw new InvalidOperationException("The fixture has not started.");
    public IPage Page => _browser?.Page ?? throw new InvalidOperationException("The browser has not started.");
    public IApp App(string name) => Brain.Get<IApp>(IApp.Key(Scope, name));
    public static IReadOnlyList<ShippedFeature> Features { get; } = new FeatureDirectory(SolutionProjects.Features).Load()
        .OrderBy(feature => feature.Owner, StringComparer.Ordinal).ThenBy(feature => feature.Bundle.Name, StringComparer.Ordinal).ToArray();

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        _brain = await host.LeaseAsync(ct);
        _person = await Brain.PersonAsync("apps-" + Guid.NewGuid().ToString("N"), ct);
        _browser = await Brain.OpenBrowserAsync(new BrowserOptions { Headless = true, AssertionTimeout = TimeSpan.FromSeconds(15) }, ct);
        await BrainFact.SignInAsync(Page, Brain, Person, ct);
        // v2 for everyone: a new brain's first connect mounts the desktop.
        await ReadyAsync("desktop", ct);
        await ClearAsync(ct);
    }

    public async Task<AppSnapshot> LoadAsync(ShippedFeature feature, CancellationToken ct)
    {
        using var caller = Person.AsCallerIn(Scope);
        var watch = Stopwatch.StartNew();
        var app = App(feature.Bundle.Name);
        var current = await app.Read().WaitAsync(ct);
        var origin = new AppOrigin(feature.Owner, new AppSource.Shipped(), feature.Bundle.Name, feature.Bundle.Version);
        var imported = await (string.IsNullOrEmpty(current.Manifest.Title)
            ? app.Import(feature.Bundle, origin) : app.Replace(feature.Bundle, origin)).WaitAsync(ct);
        Assert.True(imported.Status == AppStatus.Live, $"{feature.Bundle.Name}: {imported.Status}: {string.Join(" | ", imported.Activity)}");
        Assert.Equal(feature.Bundle.Version, imported.Version);
        var ready = await TestWait.UntilAsync(_ => app.Read(), snapshot =>
        {
            Assert.Null(snapshot.Presentation?.Error);
            return snapshot.Presentation?.Root is not null;
        }, TimeSpan.FromSeconds(30), ct);
        Assert.True(ready.Presentation!.Root!.Main is not null || ready.Presentation.Root.Views is { Length: > 0 },
            $"{feature.Bundle.Name} published no renderable view.");
        if (ready.Presentation.Root.Sphere is { } sphere) { Assert.False(string.IsNullOrWhiteSpace(sphere.Icon)); }
        Console.WriteLine($"[app-e2e] {feature.Owner}/{feature.Bundle.Name} import/presentation: {watch.Elapsed.TotalSeconds:F2}s");
        return ready;
    }

    // Opens the app the way a person does: a desktop window from the Apps window's Open, which hands the root's
    // receiver (the Workspace app) its workspace activation (or, for a service app the desktop never windows,
    // the classic Workspace's navigation).
    public async Task ShowAsync(string name, CancellationToken ct)
    {
        using var caller = Person.AsCallerIn(Scope);
        if (ClassicOnly.Contains(name))
        {
            await LayoutAsync("classic", ct);
            var state = await Brain.Get<IWorkspace>(Scope).Read().WaitAsync(ct);
            await App("workspace").Request("navigate", JsonSerializer.Serialize(new { revision = state.Revision, action = "open", id = name })).WaitAsync(ct);
        }
        else
        {
            await LayoutAsync("desktop", ct);
            await App("workspace").Request("activate", JsonSerializer.Serialize(new { kind = "workspace", brainId = Scope, application = name })).WaitAsync(ct);
            // The app's tab id is its name; its window (w-<n>) has the one viewport.
            var placed = await TestWait.UntilAsync(_ => Brain.Get<IWorkspace>(Scope).Read(), value => value.Windows.Any(window => window.Tabs.Any(tab => tab.Id == name)), Patience, ct);
            var window = placed.Windows.First(window => window.Tabs.Any(tab => tab.Id == name)).Id;
            await TestWait.UntilAsync(_ => Brain.Get<IntoChat.OS.Flutter.Viewport.IViewport>(IApp.Key(Scope, "desktop") + "/window/" + window).Read(),
                viewport => viewport.Target is not null, Patience, ct);
        }
        await TestWait.UntilAsync(_ => Brain.Get<IWorkspace>(Scope).Read(), value => value.FocusedViewId == name, TimeSpan.FromSeconds(15), ct);
    }

    // The layout the page shows for the current scope: the desktop's layout text, mounted by reloading.
    public async Task LayoutAsync(string layout, CancellationToken ct)
    {
        if (_layouts.GetValueOrDefault(Scope) == layout) { return; }
        await SetLayoutAsync(Scope, layout, ct);
        await Page.ReloadAsync();
        await ReadyAsync(layout, ct);
    }

    // Waits for the root's own control in the browser: the dock's Apps, or the classic Workspace's New desktop.
    public async Task ReadyAsync(string layout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var marker = layout == "desktop" ? "Apps" : "New desktop";
        // First: an open Apps window's title and its dock entry are buttons named Apps too.
        await Assertions.Expect(Page.GetByRole(AriaRole.Button, new() { Name = marker, Exact = true }).First).ToBeVisibleAsync(new() { Timeout = 60_000 });
        _layouts[Scope] = layout;
    }

    // What a click on the desktop does: activate the published button (the shell posts the same event).
    public async Task ActivateAsync(Func<UiNode, bool> match, CancellationToken ct)
    {
        using var caller = Person.AsCallerIn(Scope);
        var view = Brain.Get<IView>(IApp.Key(Scope, "desktop") + "/view");
        var published = await TestWait.UntilAsync(_ => view.Read(),
            current => current is not null && Flatten(current.Root).Any(node => node.Kind == UiNodeKind.Button && match(node)), Patience, ct);
        var button = Flatten(published!.Root).First(node => node.Kind == UiNodeKind.Button && match(node));
        await view.Activate(published.Generation, button.Key, button.Action!, Guid.NewGuid()).WaitAsync(ct);
    }

    public async Task<UiNode[]> DesktopNodesAsync(CancellationToken ct)
    {
        using var caller = Person.AsCallerIn(Scope);
        var view = await Brain.Get<IView>(IApp.Key(Scope, "desktop") + "/view").Read().WaitAsync(ct);
        return view is null ? [] : [.. Flatten(view.Root)];
    }

    private static IEnumerable<UiNode> Flatten(UiNode root)
    {
        yield return root;
        foreach (var child in root.Children.SelectMany(Flatten)) { yield return child; }
    }

    public async Task PrepareAsync(string name, CancellationToken ct)
    {
        EnsureUsable();
        if (name != "circleroom") { return; }
        using var caller = Person.AsCaller();
        var circle = await Brain.Get<ICircleFactory>(ICircleFactory.Key(Person.BrainId)).Create("e2e-room").WaitAsync(ct);
        _ownedCircle = circle;
        // The room is a service app: the circle's brain keeps the classic layout.
        await SetLayoutAsync(circle, "classic", ct);
        await LoadAsync(Features.Single(feature => feature.Bundle.Name == "circle"), ct);
        await ShowAsync("circle", ct);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Circles", Exact = true }).ClickAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "e2e-room", Exact = true }).ClickAsync();
        _circle = circle;
        // The shell mounted the room's brain: its classic Workspace.
        await ReadyAsync("classic", ct);
        using var member = Person.AsCallerIn(circle);
        await TestWait.UntilAsync(_ => App("workspace").Read(), snapshot => snapshot.Presentation is { Root: not null, Error: null }, TimeSpan.FromSeconds(30), ct);
    }

    // Stores the layout the scope's next connect mounts (the desktop app is installed first, as connect would).
    private async Task SetLayoutAsync(string scope, string layout, CancellationToken ct)
    {
        using var caller = Person.AsCallerIn(scope);
        var desktop = Brain.Get<IApp>(IApp.Key(scope, "desktop"));
        if ((await desktop.Read().WaitAsync(ct)).Manifest.Title.Length == 0)
        {
            var feature = Features.Single(item => item.Owner == "os" && item.Bundle.Name == "desktop");
            await desktop.Import(feature.Bundle, new AppOrigin(feature.Owner, new AppSource.Shipped(), feature.Bundle.Name, feature.Bundle.Version)).WaitAsync(ct);
        }
        await Brain.Get<IText>(IApp.Key(scope, "desktop") + "/layout").Set(layout).WaitAsync(ct);
        _layouts.Remove(scope);
    }

    public async Task FinishAsync(CancellationToken ct)
    {
        await ClearAsync(ct);
        if (_ownedCircle is null) { return; }
        await AppTestCleanup.RetireAsync(Brain, Person, _ownedCircle, ct);
        using (Person.AsCallerIn(_ownedCircle)) { await Brain.Get<ICircle>(ICircle.Key(_ownedCircle)).Remove(Person.AccountId).WaitAsync(ct); }
        _layouts.Remove(_ownedCircle);
        _ownedCircle = null;
        _circle = null;
        // The reload below mounts whatever the person's own brain now shows: mount it again before relying on it.
        _layouts.Remove(Person.BrainId);
        await Page.ReloadAsync();
        using var caller = Person.AsCaller();
        await TestWait.UntilAsync(_ => App("workspace").Read(), snapshot => snapshot.Presentation is { Root: not null, Error: null }, TimeSpan.FromSeconds(30), ct);
        await ClearAsync(ct);
    }

    public async Task ClearAsync(CancellationToken ct)
    {
        using var caller = Person.AsCallerIn(Scope);
        var watch = Stopwatch.StartNew();
        var workspace = Brain.Get<IWorkspace>(Scope);
        var state = await workspace.Read().WaitAsync(ct);
        foreach (var view in state.OpenViews)
        {
            var current = await workspace.Read().WaitAsync(ct);
            await App("workspace").Request("navigate", JsonSerializer.Serialize(new { revision = current.Revision, action = "close", id = view.Id })).WaitAsync(ct);
            await TestWait.UntilAsync(_ => workspace.Read(), value => value.OpenViews.All(open => open.Id != view.Id), TimeSpan.FromSeconds(10), ct);
        }
        foreach (var pin in state.Pins)
        {
            var current = await workspace.Read().WaitAsync(ct);
            await App("workspace").Request("navigate", JsonSerializer.Serialize(new { revision = current.Revision, action = "unpin", id = pin.Id })).WaitAsync(ct);
            await TestWait.UntilAsync(_ => workspace.Read(), value => value.Pins.All(item => item.Id != pin.Id), TimeSpan.FromSeconds(10), ct);
        }
        var names = new List<string>();
        await foreach (var instance in Brain.Kernel.Instances<IApp>(ct))
        {
            if (IApp.NameOf(Scope, instance.Key) is { } name && name is not ("workspace" or "desktop")) { names.Add(name); }
        }
        foreach (var name in names) { await App(name).Delete().WaitAsync(ct); }
        Console.WriteLine($"[app-e2e] removed {names.Count} apps: {watch.Elapsed.TotalSeconds:F2}s");
    }

    public async ValueTask DisposeAsync()
    {
        var errors = new List<Exception>();
        try
        {
            if (_browser is not null)
            {
                try { await _browser.DisposeAsync(); }
                catch (Exception error) { errors.Add(error); }
            }
            if (_brain is not null && _person is not null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                foreach (var scope in new[] { _ownedCircle, Person.BrainId }.OfType<string>())
                {
                    try { await AppTestCleanup.RetireAsync(Brain, Person, scope, cleanup.Token); }
                    catch (Exception error) { errors.Add(error); }
                }
            }
        }
        finally
        {
            _person?.Dispose();
            if (_brain is not null) { await _brain.DisposeAsync(); }
        }
        if (errors.Count > 0) { throw new AggregateException("App fixture cleanup failed.", errors); }
    }
}
