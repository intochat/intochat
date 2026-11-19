using IntoChat.OS.Apps.Gallery;
using IntoChat.OS.Tests.Browser.Scenarios;
using Microsoft.Playwright;

namespace IntoChat.OS.Tests.Browser;

/// <summary>
/// End-to-end Playwright browser tests driving live `IntoChat.OS.Modules.UI` neurons
/// rendered inside the WinUI 3-style Gallery via Flutter Web semantics (`?semantics=true`).
/// Run with `INTOCHAT_E2E_HEADED=1` to watch Playwright drive the controls live.
/// </summary>
[Collection(nameof(WebShellCollection))]
public sealed class UiNeuronGalleryBrowserFacts(WebShellHost host)
{
    [Fact]
    public async Task Gallery_renders_and_drives_live_ui_neurons_without_http_errors()
    {
        var ct = TestContext.Current.CancellationToken;
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(BrowserLaunch.Options(Environment.GetEnvironmentVariable));
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1600, Height = 1000 } });
        var page = await context.NewPageAsync();

        var httpErrors = new List<string>();
        page.Response += (_, response) =>
        {
            if (response.Status >= 400)
            {
                httpErrors.Add($"{response.Status} {new Uri(response.Url).AbsolutePath}");
            }
        };

        await page.GotoAsync(host.ShellFor("apps/gallery/view"), new() { WaitUntil = WaitUntilState.DOMContentLoaded }).WaitAsync(ct);
        await page.Locator("[flt-semantics-identifier='key:nav:toggle']").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 60_000 }).WaitAsync(ct);

        try
        {
            await ExerciseGalleryNeuronsAsync(page, ct);
        }
        catch (Exception ex)
        {
            var visible = await page.EvaluateAsync<string>("() => document.querySelector('flt-semantics-host')?.innerText ?? '<no-semantics>'");
            var ids = await page.EvaluateAsync<string[]>("() => Array.from(document.querySelectorAll('[flt-semantics-identifier]')).map(e => e.getAttribute('flt-semantics-identifier')).slice(0, 60)");
            throw new Xunit.Sdk.XunitException(
                $"Gallery E2E failed: {ex.Message}\nHTTP errors: [{string.Join(", ", httpErrors)}]\nIdentifiers: [{string.Join(", ", ids)}]\nVisible text:\n{visible[..Math.Min(1200, visible.Length)]}",
                ex);
        }

        Assert.Empty(httpErrors);
        Assert.True(await page.Locator("flt-semantics-host").CountAsync() > 0);
    }

    /// <summary>
    /// Helper demonstrating end-to-end Playwright neuron driving on a live Gallery page
    /// (Toggle, Charts, Table, Expander, Tabs, ListBox).
    /// </summary>
    internal static async Task ExerciseGalleryNeuronsAsync(IPage page, CancellationToken ct = default)
    {
        await page.ClickButtonAsync("nav:toggle", ct);
        await page.ToggleAsync(GalleryApp.WifiToggleKey, ct);

        await page.ClickButtonAsync("nav:checkbox", ct);
        await page.ToggleCheckBoxAsync(GalleryApp.RememberCheckKey, ct);

        await page.ClickButtonAsync("nav:expander", ct);
        await page.ToggleExpanderAsync(GalleryApp.DemoExpanderKey, ct);

        await page.ClickButtonAsync("nav:tabs", ct);
        await page.SelectTabAsync(GalleryApp.DemoTabsKey, "Signals", ct);

        await page.ClickButtonAsync("nav:listbox", ct);
        await page.SelectListItemAsync(GalleryApp.DemoListBoxKey, "ITable", ct);

        await page.ClickButtonAsync("nav:charts", ct);
        await page.ClickButtonAsync("chart:stream-append", ct);
        await page.SwitchChartKindAsync(GalleryApp.RevenueChartKey, "Line", ct);

        await page.ClickButtonAsync("nav:table", ct);
        await page.FilterTableAsync(GalleryApp.SpansTableKey, "Modules.UI", ct);
        await page.Neuron("ui.table", GalleryApp.SpansTableKey).First.GetByText("ui.chart.append", new() { Exact = false }).First.ClickAsync(new() { Timeout = 15_000, Force = true }).WaitAsync(ct);

        await page.ClickButtonAsync("nav:winui-inputs", ct);
        await page.SelectRadioAsync(GalleryApp.DemoRadioKey, "Hybrid Local + Cloud", ct);

        await page.ClickButtonAsync("nav:winui-nav", ct);
        await page.SelectTreeNodeAsync(GalleryApp.DemoTreeViewKey, "ChatThreadNeuron.cs", ct);
        await page.ClickBreadcrumbAsync(GalleryApp.DemoBreadcrumbKey, "Documents", ct);
        await page.DismissInfoBarAsync(GalleryApp.DemoInfoBarKey, ct);

        await page.ClickButtonAsync("nav:datetime", ct);
        await page.ToggleCalendarDayAsync(GalleryApp.DemoCalendarKey, 21, ct);

        await page.ClickButtonAsync("nav:media", ct);
        await page.SelectGridItemAsync(GalleryApp.DemoGridViewKey, "hero-banner.png", ct);
        await page.ToggleMediaPlaybackAsync(GalleryApp.DemoMediaPlayerKey, ct);

        await page.ClickButtonAsync("nav:workspace", ct);
        await page.Neuron("ui.command-bar", GalleryApp.DemoCommandBarKey).First.Locator("[role='button']").First.ClickAsync(new() { Timeout = 15_000, Force = true }).WaitAsync(ct);
        await page.Neuron("ui.split-button", GalleryApp.DemoSplitButtonKey).First.Locator("[role='button']").First.ClickAsync(new() { Timeout = 15_000, Force = true }).WaitAsync(ct);
        await page.Neuron("ui.teaching-tip", GalleryApp.DemoTeachingTipKey).First.Locator("[role='button']").First.ClickAsync(new() { Timeout = 15_000, Force = true }).WaitAsync(ct);
        await page.Neuron("ui.code-editor", GalleryApp.DemoCodeEditorKey).First.Locator("[role='button']").First.ClickAsync(new() { Timeout = 15_000, Force = true }).WaitAsync(ct);
    }

    [Fact]
    public async Task Settings_files_and_ino_apps_render_live_without_http_errors()
    {
        var ct = TestContext.Current.CancellationToken;
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(BrowserLaunch.Options(Environment.GetEnvironmentVariable));
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1600, Height = 1000 } });

        foreach (var root in new[] { "apps/settings/view", "apps/files/view", "apps/ino/view" })
        {
            var page = await context.NewPageAsync();
            var httpErrors = new List<string>();
            page.Response += (_, response) =>
            {
                if (response.Status >= 400)
                {
                    httpErrors.Add($"{response.Status} {new Uri(response.Url).AbsolutePath}");
                }
            };

            await page.GotoAsync(host.ShellFor(root), new() { WaitUntil = WaitUntilState.DOMContentLoaded }).WaitAsync(ct);
            await page.Locator("[flt-semantics-identifier]").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 60_000 }).WaitAsync(ct);

            Assert.True(httpErrors.Count == 0, $"{root} returned HTTP errors: [{string.Join(", ", httpErrors)}]");
            Assert.True(await page.Locator("flt-semantics-host").CountAsync() > 0);
            await page.CloseAsync();
        }
    }
}
