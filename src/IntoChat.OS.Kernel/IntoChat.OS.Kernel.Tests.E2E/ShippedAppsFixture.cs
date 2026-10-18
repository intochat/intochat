using System.Diagnostics;
using System.Text.Json;
using IntoChat.OS.Apps;
using IntoChat.OS.Circles;
using IntoChat.OS.Flutter.Workspace;
using Microsoft.Playwright;

namespace IntoChat.OS.Kernel.Tests.E2E;

// One account and browser context per class, on the collection's already running host.
// Only the workspace shell survives between cases; subjects and support apps are removed.
public sealed class ShippedAppsFixture(ReferenceBrainFixture host) : IAsyncLifetime
{
    private ITestBrain? _brain;
    private BrowserSession? _browser;
    private Person? _person;
    private Exception? _unusable;
    private string? _circle;
    private string? _ownedCircle;
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
        using var caller = Person.AsCaller();
        await TestWait.UntilAsync(_ => App("workspace").Read(), snapshot => snapshot.Presentation is { Root: not null, Error: null }, TimeSpan.FromSeconds(60), ct);
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

    public async Task ShowAsync(string name, CancellationToken ct)
    {
        using var caller = Person.AsCallerIn(Scope);
        if (name == "workspace") { return; }
        var state = await Brain.Get<IWorkspace>(Scope).Read().WaitAsync(ct);
        await App("workspace").Request("navigate", JsonSerializer.Serialize(new { revision = state.Revision, action = "open", id = name })).WaitAsync(ct);
        await TestWait.UntilAsync(_ => Brain.Get<IWorkspace>(Scope).Read(), value => value.FocusedViewId == name, TimeSpan.FromSeconds(15), ct);
    }

    public async Task PrepareAsync(string name, CancellationToken ct)
    {
        EnsureUsable();
        if (name != "circleroom") { return; }
        using var caller = Person.AsCaller();
        var circle = await Brain.Get<ICircleFactory>(ICircleFactory.Key(Person.BrainId)).Create("e2e-room").WaitAsync(ct);
        _ownedCircle = circle;
        await LoadAsync(Features.Single(feature => feature.Bundle.Name == "circle"), ct);
        await ShowAsync("circle", ct);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Circles", Exact = true }).ClickAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "e2e-room", Exact = true }).ClickAsync();
        _circle = circle;
        using var member = Person.AsCallerIn(circle);
        await TestWait.UntilAsync(_ => App("workspace").Read(), snapshot => snapshot.Presentation is { Root: not null, Error: null }, TimeSpan.FromSeconds(30), ct);
    }

    public async Task FinishAsync(CancellationToken ct)
    {
        await ClearAsync(ct);
        if (_ownedCircle is null) { return; }
        await AppTestCleanup.RetireAsync(Brain, Person, _ownedCircle, ct);
        using (Person.AsCallerIn(_ownedCircle)) { await Brain.Get<ICircle>(ICircle.Key(_ownedCircle)).Remove(Person.AccountId).WaitAsync(ct); }
        _ownedCircle = null;
        _circle = null;
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
            if (IApp.NameOf(Scope, instance.Key) is { } name && name != "workspace") { names.Add(name); }
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
