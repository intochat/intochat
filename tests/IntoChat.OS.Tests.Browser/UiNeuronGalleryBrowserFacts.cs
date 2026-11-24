using System.Text.RegularExpressions;
using IntoChat.OS.Apps.Gallery;
using IntoChat.OS.Modules.Microsoft.Playwright;
using IntoChat.OS.Tests.Browser.Scenarios;
using Microsoft.Playwright;

namespace IntoChat.OS.Tests.Browser;

[Collection(nameof(WebShellCollection))]
public sealed partial class UiNeuronGalleryBrowserFacts(WebShellHost host)
{
    [GeneratedRegex(@"\(([1-9]\d*)\s+spans\)", RegexOptions.CultureInvariant)]
    private static partial Regex PositiveSpanCountPattern();

    [Fact]
    public async Task Gallery_renders_and_drives_live_ui_neurons_without_http_errors()
    {
        var ct = TestContext.Current.CancellationToken;
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(BrowserLaunch.Options(Environment.GetEnvironmentVariable));
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1600, Height = 1400 } });
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
        await page.Locator("[flt-semantics-identifier='key:nav:tabs']").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 60_000 }).WaitAsync(ct);

        try
        {
            await ExerciseGalleryNeuronsAsync(page, ct);
        }
        catch (Exception ex)
        {
            var visible = await page.EvaluateAsync<string>("() => document.querySelector('flt-semantics-host')?.innerText ?? '<no-semantics>'");
            var ids = await page.EvaluateAsync<string[]>("() => Array.from(document.querySelectorAll('[flt-semantics-identifier]')).map(e => e.getAttribute('flt-semantics-identifier')).slice(0, 80)");
            throw new Xunit.Sdk.XunitException(
                $"Gallery E2E failed: {ex.Message}\nHTTP errors: [{string.Join(", ", httpErrors)}]\nIdentifiers: [{string.Join(", ", ids)}]\nVisible text:\n{visible[..Math.Min(1500, visible.Length)]}",
                ex);
        }

        Assert.Empty(httpErrors);
        Assert.True(await page.Locator("flt-semantics-host").CountAsync() > 0);
    }

    internal static async Task ExerciseGalleryNeuronsAsync(IPage page, CancellationToken ct = default)
    {
        await page.ClickButtonAsync("nav:tabs", ct);
        await page.SelectTabAsync(GalleryApp.DemoTabsKey, "Signals", ct);

        await page.ClickButtonAsync("nav:charts", ct);
        await page.ClickButtonAsync("chart:stream-append", ct);
        await page.SwitchChartKindAsync(GalleryApp.RevenueChartKey, "Line", ct);

        await page.ClickButtonAsync("nav:table", ct);
        await page.FilterTableAsync(GalleryApp.SpansTableKey, "Modules.UI", ct);
        await page.Neuron("ui.table", GalleryApp.SpansTableKey).First.GetByText("ui.chart.append", new() { Exact = false }).First.ClickAsync(new() { Timeout = 15_000, Force = true }).WaitAsync(ct);

        await page.ClickButtonAsync("nav:winui-inputs", ct);
        await page.Neuron("ui.slider", GalleryApp.DemoSliderKey).First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 }).WaitAsync(ct);

        await page.ClickButtonAsync("nav:winui-nav", ct);
        await page.SelectTreeNodeAsync(GalleryApp.DemoTreeViewKey, "ChatThreadNeuron.cs", ct);
        await page.ClickBreadcrumbAsync(GalleryApp.DemoBreadcrumbKey, "Documents", ct);

        await page.ClickButtonAsync("nav:media", ct);
        await page.SelectGridItemAsync(GalleryApp.DemoGridViewKey, "hero-banner.png", ct);

        // Workspace Kit + re_editor / re_highlight syntax highlighting verification across grammars
        await page.ClickButtonAsync("nav:workspace", ct);
        await page.Neuron("ui.command-bar", GalleryApp.DemoCommandBarKey).First.Locator("[role='button']").First.ClickAsync(new() { Timeout = 15_000, Force = true }).WaitAsync(ct);

        await page.KeyedNode("code-highlight:csharp").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);
        var csharpSemantics = await page.ReadCodeEditorSemanticsAsync(GalleryApp.DemoCodeEditorKey, ct);
        Assert.Contains(csharpSemantics, s => s.Contains("Syntax highlight: csharp", StringComparison.Ordinal) && PositiveSpanCountPattern().IsMatch(s));

        await page.ClickButtonAsync("code:lang:dart", ct);
        await page.KeyedNode("code-highlight:dart").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);
        var dartSemantics = await page.ReadCodeEditorSemanticsAsync(GalleryApp.DemoCodeEditorKey, ct);
        Assert.Contains(dartSemantics, s => s.Contains("Syntax highlight: dart", StringComparison.Ordinal) && PositiveSpanCountPattern().IsMatch(s));

        await page.ClickButtonAsync("code:lang:json", ct);
        await page.KeyedNode("code-highlight:json").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);
        var jsonSemantics = await page.ReadCodeEditorSemanticsAsync(GalleryApp.DemoCodeEditorKey, ct);
        Assert.Contains(jsonSemantics, s => s.Contains("Syntax highlight: json", StringComparison.Ordinal) && PositiveSpanCountPattern().IsMatch(s));

        await page.Neuron("ui.markdown", GalleryApp.DemoMarkdownKey).First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);

        // Iconography browser (IGridView + category filter)
        await page.ClickButtonAsync("nav:icons", ct);
        await page.Neuron("ui.grid-view", GalleryApp.IconGridKey).First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 }).WaitAsync(ct);
        var iconGridText = await page.ReadNeuronTextAsync("ui.grid-view", GalleryApp.IconGridKey, ct);
        Assert.False(string.IsNullOrWhiteSpace(iconGridText));

        // WinUI 3 Wave 3 (Stepper, Kanban, PropertyGrid live over gRPC)
        await page.ClickButtonAsync("nav:wave3", ct);
        await page.Stepper(GalleryApp.DemoStepperKey).First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 }).WaitAsync(ct);
        Assert.False(string.IsNullOrWhiteSpace(await page.ReadStepperTextAsync(GalleryApp.DemoStepperKey, ct)));

        await page.KanbanBoard(GalleryApp.DemoKanbanKey).First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 }).WaitAsync(ct);
        Assert.False(string.IsNullOrWhiteSpace(await page.ReadKanbanTextAsync(GalleryApp.DemoKanbanKey, ct)));

        await page.PropertyGrid(GalleryApp.DemoPropertyGridKey).First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);
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

            if (root == "apps/settings/view")
            {
                await page.KeyedNode("settings-shell").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);
            }

            Assert.True(httpErrors.Count == 0, $"{root} returned HTTP errors: [{string.Join(", ", httpErrors)}]");
            Assert.True(await page.Locator("flt-semantics-host").CountAsync() > 0);
            await page.CloseAsync();
        }
    }
}
