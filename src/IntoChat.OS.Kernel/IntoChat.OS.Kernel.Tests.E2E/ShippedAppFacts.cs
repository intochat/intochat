using System.Diagnostics;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Workspace;
using Microsoft.Playwright;

namespace IntoChat.OS.Kernel.Tests.E2E;

// Each folder is its own result. xUnit runs these cases serially on one browser and host.
[Collection("Reference host")]
public sealed class ShippedAppFacts(ShippedAppsFixture fixture) : IClassFixture<ShippedAppsFixture>
{
    // The two roots are not windowed apps: the journeys below cover the desktop and the classic Workspace.
    public static TheoryData<string, string> ShippedApps => new(ShippedAppsFixture.Features
        .Where(feature => feature.Bundle.Name is not ("desktop" or "workspace")).Select(feature => (feature.Owner, feature.Bundle.Name)));

    [Theory(Timeout = 120_000)]
    [MemberData(nameof(ShippedApps))]
    public Task FolderAppRunsInTheHeadlessBrowser(string owner, string name) => RunAsync(name, TestContext.Current.CancellationToken, async ct =>
    {
        await fixture.PrepareAsync(name, ct);
        await fixture.LoadAsync(Feature(owner, name), ct);
        await fixture.ShowAsync(name, ct);
        await ShippedAppJourneys.ExerciseAsync(fixture, name, ct);
    });

    // v1 stays covered: switched to classic, the Workspace root shows.
    [Fact(Timeout = 120_000)]
    public Task TheClassicLayoutStillShowsTheWorkspace() => RunAsync("workspace", TestContext.Current.CancellationToken, async ct =>
    {
        await fixture.LayoutAsync("classic", ct);
        await ShippedAppJourneys.ExerciseAsync(fixture, "workspace", ct);
    });

    // The v2 journey in the real shell: Ino's greeting, the dock's Apps window, Install there, a third window, close,
    // then the More menu's Classic layout and v1's New desktop (the shell's reconnect both ways).
    // DesktopStartFacts, DesktopWindowFacts and DesktopLayoutSwitchFacts cover each step's state.
    [Fact(Timeout = 240_000)]
    public Task TheDesktopJourneyGreetsInstallsAnAppAndSwitchesToClassicAndBack() => RunAsync("desktop", TestContext.Current.CancellationToken, async ct =>
    {
        var page = fixture.Page;
        ILocator Button(string name) => page.GetByRole(AriaRole.Button, new() { Name = name, Exact = true });
        Task Visible(ILocator locator) => Assertions.Expect(locator).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await fixture.LayoutAsync("desktop", ct);
        // Each case starts with only the roots installed: put Ino back, then start the desktop again.
        await fixture.LoadAsync(Feature("os", "ino"), ct);
        await page.ReloadAsync();
        await fixture.ReadyAsync("desktop", ct);
        // start.cs re-renders the dock after the reload's connect: wait for Ino's button there.
        await TestWait.UntilAsync(fixture.DesktopNodesAsync, nodes => nodes.Any(node => node.Key == "dock-ino"), TimeSpan.FromSeconds(60), ct);

        // Ino is first in the dock; its window shows the one greeting, which points to the gallery.
        // The dock's Ino and, once Ino's window shows, its header title: either focuses Ino.
        await Button("Ino").First.ClickAsync();
        await Visible(page.GetByText("Open apps from the dock, or just tell me", new() { Exact = false }).First);

        // The dock's Apps opens the Apps window (its library; the desktop has no gallery).
        await Button("Apps").ClickAsync();
        await Visible(Button("Details").First);
        // The built-in Calculator's detail (the link form a share uses), then its Install: the shell installs it
        // as the person and opens it in a third window.
        using (fixture.Person.AsCallerIn(fixture.Scope)) { await fixture.App("apps").Request("open", "app:os/calculator").WaitAsync(ct); }
        await Button("Install").ClickAsync();
        using (fixture.Person.AsCallerIn(fixture.Scope))
        {
            await TestWait.UntilAsync(_ => fixture.Brain.Get<IWorkspace>(IWorkspace.Key(fixture.Scope)).Read(),
                state => state.FocusedViewId == "calculator" && state.OpenViews.Any(view => view.Id == "ino") && state.OpenViews.Any(view => view.Id == "apps"),
                TimeSpan.FromSeconds(60), ct);
        }
        await Visible(Button("="));
        await Visible(Button("New chat").First);
        await Assertions.Expect(Button("Close tab")).ToHaveCountAsync(3);

        // Close the last window's only tab (windows sit in the order they opened: Ino, Apps, then Calculator).
        await Button("Close tab").Last.ClickAsync();
        await Assertions.Expect(Button("=")).ToHaveCountAsync(0, new() { Timeout = 30_000 });
        await Visible(Button("New chat").First);

        // More holds Classic layout; v1 offers New desktop back.
        await Button("Classic layout").ClickAsync();
        await fixture.ReadyAsync("classic", ct);
        await Button("New desktop").ClickAsync();
        await fixture.ReadyAsync("desktop", ct);
        await Visible(Button("New chat").First);
    });

    private async Task RunAsync(string name, CancellationToken test, Func<CancellationToken, Task> journey)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(test);
        budget.CancelAfter(TimeSpan.FromSeconds(name == "desktop" ? 210 : 90));
        var ct = budget.Token;
        using var caller = fixture.Person.AsCaller();
        var watch = Stopwatch.StartNew();
        fixture.EnsureUsable();
        Exception? failure = null;
        try
        {
            await journey(ct);
            Console.WriteLine($"[app-e2e] {name} complete: {watch.Elapsed.TotalSeconds:F2}s");
        }
        catch (Exception error)
        {
            failure = error;
            try
            {
                var path = Path.Combine(Path.GetTempPath(), $"intochat-os-app-{name}.png");
                await fixture.Page.ScreenshotAsync(new() { Path = path, Timeout = 5_000 });
                Console.WriteLine($"[app-e2e] failure screenshot: {path}");
            }
            catch (Exception capture) { error.Data["ScreenshotFailure"] = capture.Message; }
            if (ct.IsCancellationRequested) { fixture.Poison(error); }
            throw;
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await fixture.FinishAsync(cleanup.Token); }
            catch (Exception error)
            {
                fixture.Poison(error);
                if (failure is null) { throw; }
                failure.Data["AppCleanupFailure"] = error.ToString();
                Console.WriteLine($"[app-e2e] cleanup also failed: {error}");
            }
        }
    }

    private static ShippedFeature Feature(string owner, string name)
        => ShippedAppsFixture.Features.Single(feature => feature.Owner == owner && feature.Bundle.Name == name);
}
