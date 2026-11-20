using System.Globalization;
using IntoChat.OS.Modules.Microsoft.Playwright;
using IntoChat.OS.Tests.Browser.Scenarios;
using Microsoft.Playwright;

namespace IntoChat.OS.Tests.Browser;

[Collection(nameof(WebShellCollection))]
public sealed class AspireAndOrleansBrowserFacts(WebShellHost host)
{
    private const int OperatorViewportWidth = 1440;
    private const int OperatorViewportHeight = 900;

    [Fact]
    public async Task Aspire_and_orleans_operator_apps_render_full_bleed_web_browsers_and_switch_routes_without_http_errors()
    {
        var ct = TestContext.Current.CancellationToken;
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(BrowserLaunch.Options(Environment.GetEnvironmentVariable));
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = OperatorViewportWidth, Height = OperatorViewportHeight } });

        // 1. Aspire (apps/aspire/view): full-bleed WebBrowser (>= 70% viewport height = >= 630px at 900px) + route switching (Resources -> Traces -> Metrics)
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

            await page.GotoAsync(host.ShellFor("apps/aspire/view"), new() { WaitUntil = WaitUntilState.DOMContentLoaded }).WaitAsync(ct);
            await page.KeyedNode("aspire-shell").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 60_000 }).WaitAsync(ct);

            try
            {
                var aspireBox = await page.MeasureKeyedBoxAsync("aspire-browser", minWidth: 900f, minHeight: 630f, ct);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[GEOMETRY] Aspire WebBrowser Surface: {aspireBox.Format(OperatorViewportWidth, OperatorViewportHeight)}"));
                Assert.True(
                    aspireBox.HeightRatio(OperatorViewportHeight) >= 0.70f && aspireBox.Height >= 630f,
                    string.Create(CultureInfo.InvariantCulture, $"Aspire WebBrowser must occupy >= 70% of 900px viewport (>= 630px), measured: {aspireBox.Format(OperatorViewportWidth, OperatorViewportHeight)}"));
                Assert.True(
                    aspireBox.WidthRatio(OperatorViewportWidth) >= 0.85f,
                    string.Create(CultureInfo.InvariantCulture, $"Aspire WebBrowser must span full-bleed width (>= 85%w), measured: {aspireBox.Format(OperatorViewportWidth, OperatorViewportHeight)}"));

                await page.ClickButtonAsync("aspire:section:traces", ct);
                await WaitForIframeSrcContainsAsync(page, "/traces", ct);

                await page.ClickButtonAsync("aspire:section:metrics", ct);
                await WaitForIframeSrcContainsAsync(page, "/metrics", ct);

                await page.ClickButtonAsync("aspire:reload", ct);
                await WaitForIframeSrcContainsAsync(page, "reload=1", ct);
            }
            catch (Exception ex)
            {
                var visible = await page.EvaluateAsync<string>("() => document.querySelector('flt-semantics-host')?.innerText ?? '<no-semantics>'");
                var ids = await page.ReadSemanticsIdentifiersAsync(ct);
                throw new Xunit.Sdk.XunitException(
                    $"Aspire Browser E2E failed: {ex.Message}\nHTTP errors: [{string.Join(", ", httpErrors)}]\nIdentifiers: [{string.Join(", ", ids.Take(80))}]\nVisible text:\n{visible[..Math.Min(1500, visible.Length)]}",
                    ex);
            }

            Assert.Empty(httpErrors);
            await page.CloseAsync();
        }

        // 2. Orleans (apps/orleans/view): full-bleed WebBrowser (>= 70% viewport height = >= 630px at 900px) + co-hosted /orleans route switching (Overview -> Grains -> Silos)
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

            await page.GotoAsync(host.ShellFor("apps/orleans/view"), new() { WaitUntil = WaitUntilState.DOMContentLoaded }).WaitAsync(ct);
            await page.KeyedNode("orleans-shell").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 60_000 }).WaitAsync(ct);

            try
            {
                var orleansBox = await page.MeasureKeyedBoxAsync("orleans-browser", minWidth: 900f, minHeight: 630f, ct);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[GEOMETRY] Orleans WebBrowser Surface: {orleansBox.Format(OperatorViewportWidth, OperatorViewportHeight)}"));
                Assert.True(
                    orleansBox.HeightRatio(OperatorViewportHeight) >= 0.70f && orleansBox.Height >= 630f,
                    string.Create(CultureInfo.InvariantCulture, $"Orleans WebBrowser must occupy >= 70% of 900px viewport (>= 630px), measured: {orleansBox.Format(OperatorViewportWidth, OperatorViewportHeight)}"));
                Assert.True(
                    orleansBox.WidthRatio(OperatorViewportWidth) >= 0.85f,
                    string.Create(CultureInfo.InvariantCulture, $"Orleans WebBrowser must span full-bleed width (>= 85%w), measured: {orleansBox.Format(OperatorViewportWidth, OperatorViewportHeight)}"));

                await WaitForIframeSrcContainsAsync(page, "/orleans#/overview", ct);

                await page.ClickButtonAsync("orleans:section:grains", ct);
                await WaitForIframeSrcContainsAsync(page, "#/grains", ct);

                await page.ClickButtonAsync("orleans:section:silos", ct);
                await WaitForIframeSrcContainsAsync(page, "#/silos", ct);

                await page.ClickButtonAsync("orleans:reload", ct);
                await WaitForIframeSrcContainsAsync(page, "reload=1", ct);
            }
            catch (Exception ex)
            {
                var visible = await page.EvaluateAsync<string>("() => document.querySelector('flt-semantics-host')?.innerText ?? '<no-semantics>'");
                var ids = await page.ReadSemanticsIdentifiersAsync(ct);
                throw new Xunit.Sdk.XunitException(
                    $"Orleans Browser E2E failed: {ex.Message}\nHTTP errors: [{string.Join(", ", httpErrors)}]\nIdentifiers: [{string.Join(", ", ids.Take(80))}]\nVisible text:\n{visible[..Math.Min(1500, visible.Length)]}",
                    ex);
            }

            Assert.Empty(httpErrors);
            await page.CloseAsync();
        }
    }

    [Fact]
    public async Task Engineer_cross_window_deep_links_open_aspire_traces_and_orleans_grains_in_live_shell_without_http_errors()
    {
        var ct = TestContext.Current.CancellationToken;
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(BrowserLaunch.Options(Environment.GetEnvironmentVariable));
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = OperatorViewportWidth, Height = OperatorViewportHeight } });
        // 1. Engineer -> Aspire cross-window deep-link (engineer:open-aspire -> ui.navigate-from + ui.navigate-to -> /traces)
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

            try
            {
                await page.GotoAsync(host.ShellFor("apps/engineer/view"), new() { WaitUntil = WaitUntilState.DOMContentLoaded }).WaitAsync(ct);
                await page.KeyedNode("engineer-shell").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 60_000 }).WaitAsync(ct);

                await page.ClickButtonAsync("engineer:open-aspire", ct);
                await page.KeyedNode("aspire-shell").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 30_000 }).WaitAsync(ct);

                var deepLinkedAspireBox = await page.MeasureKeyedBoxAsync("aspire-browser", minWidth: 900f, minHeight: 630f, ct);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[GEOMETRY] Engineer->Aspire Deep-Link Surface: {deepLinkedAspireBox.Format(OperatorViewportWidth, OperatorViewportHeight)}"));
                Assert.True(deepLinkedAspireBox.HeightRatio(OperatorViewportHeight) >= 0.70f);
                await WaitForIframeSrcContainsAsync(page, "/traces", ct);

                // Restore default section for subsequent tests sharing WebShellHost
                await page.ClickButtonAsync("aspire:section:resources", ct);
            }
            catch (Exception ex)
            {
                var visible = await page.EvaluateAsync<string>("() => document.querySelector('flt-semantics-host')?.innerText ?? '<no-semantics>'");
                var ids = await page.ReadSemanticsIdentifiersAsync(ct);
                throw new Xunit.Sdk.XunitException(
                    $"Engineer->Aspire cross-window deep-link E2E failed: {ex.Message}\nHTTP errors: [{string.Join(", ", httpErrors)}]\nIdentifiers: [{string.Join(", ", ids.Take(80))}]\nVisible text:\n{visible[..Math.Min(1500, visible.Length)]}",
                    ex);
            }

            Assert.Empty(httpErrors);
            await page.CloseAsync();
        }

        // 2. Engineer -> Orleans cross-window deep-link (engineer:open-orleans -> ui.navigate-from + ui.navigate-to -> #/grains/EngineerFlow)
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

            try
            {
                await page.GotoAsync(host.ShellFor("apps/engineer/view"), new() { WaitUntil = WaitUntilState.DOMContentLoaded }).WaitAsync(ct);
                await page.KeyedNode("engineer-shell").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 60_000 }).WaitAsync(ct);

                await page.ClickButtonAsync("engineer:open-orleans", ct);
                await page.KeyedNode("orleans-shell").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 30_000 }).WaitAsync(ct);

                var deepLinkedOrleansBox = await page.MeasureKeyedBoxAsync("orleans-browser", minWidth: 900f, minHeight: 630f, ct);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[GEOMETRY] Engineer->Orleans Deep-Link Surface: {deepLinkedOrleansBox.Format(OperatorViewportWidth, OperatorViewportHeight)}"));
                Assert.True(deepLinkedOrleansBox.HeightRatio(OperatorViewportHeight) >= 0.70f);
                await WaitForIframeSrcContainsAsync(page, "#/grains/EngineerFlow", ct);

                // Restore default overview route for subsequent tests sharing WebShellHost
                await page.ClickButtonAsync("orleans:clear-filter", ct);
                await page.ClickButtonAsync("orleans:section:overview", ct);
                await WaitForIframeSrcContainsAsync(page, "/orleans#/overview", ct);
            }
            catch (Exception ex)
            {
                var visible = await page.EvaluateAsync<string>("() => document.querySelector('flt-semantics-host')?.innerText ?? '<no-semantics>'");
                var ids = await page.ReadSemanticsIdentifiersAsync(ct);
                throw new Xunit.Sdk.XunitException(
                    $"Engineer->Orleans cross-window deep-link E2E failed: {ex.Message}\nHTTP errors: [{string.Join(", ", httpErrors)}]\nIdentifiers: [{string.Join(", ", ids.Take(80))}]\nVisible text:\n{visible[..Math.Min(1500, visible.Length)]}",
                    ex);
            }

            Assert.Empty(httpErrors);
            await page.CloseAsync();
        }
    }

    private static async Task WaitForIframeSrcContainsAsync(IPage page, string expectedSubstring, CancellationToken ct)
    {
        for (var i = 0; i < 60; i++)
        {
            var srcs = await page.EvaluateAsync<string[]>("() => Array.from(document.querySelectorAll('iframe')).map(f => f.src || '')").WaitAsync(ct);
            if (srcs.Any(s => s.Contains(expectedSubstring, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }
            await page.WaitForTimeoutAsync(150).WaitAsync(ct);
        }

        var finalSrcs = await page.EvaluateAsync<string[]>("() => Array.from(document.querySelectorAll('iframe')).map(f => f.src || '')").WaitAsync(ct);
        Assert.Contains(finalSrcs, s => s.Contains(expectedSubstring, StringComparison.OrdinalIgnoreCase));
    }
}
