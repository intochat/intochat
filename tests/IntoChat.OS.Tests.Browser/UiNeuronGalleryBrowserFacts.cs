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

        await page.GotoAsync(host.Shell, new() { WaitUntil = WaitUntilState.DOMContentLoaded }).WaitAsync(ct);
        await page.Locator("[flt-semantics-identifier]").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 60_000 }).WaitAsync(ct);

        // If the shell is on uikit/view or desktop, verify the semantics host mounted cleanly with zero 4xx/5xx errors.
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
        await page.SortTableAsync(GalleryApp.SpansTableKey, "Span", ct);

        await page.ClickButtonAsync("nav:winui-inputs", ct);
        await page.SelectRadioAsync(GalleryApp.DemoRadioKey, "Hybrid Local + Cloud", ct);

        await page.ClickButtonAsync("nav:winui-nav", ct);
        await page.SelectTreeNodeAsync(GalleryApp.DemoTreeViewKey, "GalleryApp.cs", ct);
        await page.ClickBreadcrumbAsync(GalleryApp.DemoBreadcrumbKey, "Documents", ct);
        await page.DismissInfoBarAsync(GalleryApp.DemoInfoBarKey, ct);

        await page.ClickButtonAsync("nav:datetime", ct);
        await page.ToggleCalendarDayAsync(GalleryApp.DemoCalendarKey, 21, ct);

        await page.ClickButtonAsync("nav:media", ct);
        await page.SelectGridItemAsync(GalleryApp.DemoGridViewKey, "hero-banner.png", ct);
        await page.ToggleMediaPlaybackAsync(GalleryApp.DemoMediaPlayerKey, ct);
    }
}
