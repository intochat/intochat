using System.Globalization;
using IntoChat.OS.Apps.Creator;
using IntoChat.OS.Modules.Microsoft.Playwright;
using IntoChat.OS.Tests.Browser.Scenarios;
using Microsoft.Playwright;

namespace IntoChat.OS.Tests.Browser;

[Collection(nameof(WebShellCollection))]
public sealed class EngineerAndCreatorBrowserFacts(WebShellHost host)
{
    private const int StudioViewportWidth = 1440;
    private const int StudioViewportHeight = 900;

    [Fact]
    public async Task Creator_flame_camera_hud_minimap_shell_collapse_and_geometry_work_live_without_http_errors()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = await host.NewBrowserContextAsync(StudioViewportWidth, StudioViewportHeight, ct);
        var page = await context.NewPageAsync();

        var httpErrors = new List<string>();
        page.Response += (_, response) =>
        {
            if (response.Status >= 400)
            {
                httpErrors.Add($"{response.Status} {new Uri(response.Url).AbsolutePath}");
            }
        };

        await page.GotoAsync(host.ShellFor("apps/creator/view"), new() { WaitUntil = WaitUntilState.DOMContentLoaded }).WaitAsync(ct);
        await page.KeyedNode("creator-shell").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 60_000 }).WaitAsync(ct);
        await page.SceneCameraHud().First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 60_000 }).WaitAsync(ct);

        try
        {
            // 1. Hard BoundingBoxAsync geometry & zero-bloat assertions at 1440x900
            var canvasBox = await page.MeasureSceneCanvasBoxAsync("brain", ct);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[GEOMETRY] Creator Flame 3D Canvas: {canvasBox.Format(StudioViewportWidth, StudioViewportHeight)}"));
            Assert.True(
                canvasBox.HeightRatio(StudioViewportHeight) >= 0.65f && canvasBox.Height >= 580f,
                string.Create(CultureInfo.InvariantCulture, $"Creator Flame canvas must occupy >= 65% of 900px viewport (>= 580px), measured: {canvasBox.Format(StudioViewportWidth, StudioViewportHeight)}"));
            Assert.True(
                canvasBox.Y <= 110f,
                string.Create(CultureInfo.InvariantCulture, $"Creator top toolbar must stay compact (canvas Y <= 110px), measured: {canvasBox.Format(StudioViewportWidth, StudioViewportHeight)}"));

            // One inspector tab strip child and no ui.* neuron: the state tab is a jsonTree node, the source tab a code block
            foreach (var oldKind in new[] { "ui.command-bar", "ui.tabs", "ui.slider", "ui.property-grid", "ui.code-editor", "ui.table" })
            {
                Assert.Equal(0, await page.SemanticsPrefix($"neuron:{oldKind}:").CountAsync().WaitAsync(ct));
            }
            Assert.Equal(1, await page.ChildNode(CreatorView.InspectorTabsKey).CountAsync().WaitAsync(ct));

            // 2. Corner Viewport Minimap + Follow Live HUD toggle
            var minimapBox = await page.MeasureKeyedBoxAsync("flame-viewport-minimap", minWidth: 80f, minHeight: 40f, ct);
            Assert.True(
                minimapBox.Width >= 80f && minimapBox.Height >= 40f,
                string.Create(CultureInfo.InvariantCulture, $"Expected corner viewport minimap bounds, measured: {minimapBox.Format(StudioViewportWidth, StudioViewportHeight)}"));
            await page.SceneFollowLive().First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);
            await page.ToggleSceneFollowLiveAsync(ct);
            await page.ClickSceneMinimapAsync(0.45f, 0.5f, ct);

            // 3. Flame Camera HUD: normalize to 100%, zoom in (-> 125%), zoom out, drag pan, fit, and reset back to 100%
            await page.ResetSceneCameraAsync(ct);
            var baselineZoom = await WaitForZoomTargetAsync(page, "100%", ct);
            Assert.Equal("100%", baselineZoom);

            await page.ZoomSceneInAsync(ct);
            var zoomedIn = await WaitForZoomChangeAsync(page, "100%", ct);
            Assert.Equal("125%", zoomedIn);

            await page.ZoomSceneOutAsync(ct);
            await page.PanSceneByDragAsync(80, 40, ct);
            await page.FitSceneCameraAsync(ct);
            await page.ResetSceneCameraAsync(ct);
            var resetZoom = await WaitForZoomTargetAsync(page, "100%", ct);
            Assert.Equal("100%", resetZoom);

            // 4. AppShell collapsible right inspector: collapse widens canvas, re-expand restores inspector
            await page.ShellToggleInspector().First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);
            var widthWithInspector = (await page.MeasureSceneCanvasBoxAsync("brain", ct)).Width;
            await page.ToggleShellInspectorAsync(ct);
            var widthCollapsed = await WaitForCanvasWidthChangeAsync(page, widthWithInspector, expectWider: true, ct);
            Assert.True(
                widthCollapsed > widthWithInspector + 120f,
                string.Create(CultureInfo.InvariantCulture, $"Collapsing inspector must widen 3D canvas (before={widthWithInspector:0.#}px, collapsed={widthCollapsed:0.#}px)."));

            await page.ToggleShellInspectorAsync(ct);
            await page.ChildNode(CreatorView.InspectorTabsKey).First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);
            var widthRestored = await WaitForCanvasWidthChangeAsync(page, widthCollapsed, expectWider: false, ct);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[GEOMETRY] Creator Inspector Collapse/Expand Widths: open={widthWithInspector:0.#}px -> collapsed={widthCollapsed:0.#}px -> restored={widthRestored:0.#}px"));
            Assert.True(
                widthRestored < widthCollapsed - 100f,
                string.Create(CultureInfo.InvariantCulture, $"Re-expanding inspector must restore split width (collapsed={widthCollapsed:0.#}px, restored={widthRestored:0.#}px)."));

            // 5. Creator Time-Travel controls + keyboard stepping + Inspector tabs (State, Source, Signals, State Graph, Scenarios)
            await page.KeyedNode("replay-cursor-readout").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);
            await page.ChildNode(CreatorView.ReplaySliderKey).First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);

            await page.StepTimeTravelBackwardAsync(ct);
            await page.StepTimeTravelForwardAsync(ct);
            await page.JumpTimeTravelFirstAsync(ct);
            await page.JumpTimeTravelLastAsync(ct);
            await page.ToggleTimeTravelModeAsync(ct);

            await SelectInspectorTabAndWaitAsync(page, "Source", page.GetByText("No source files kept for the current selection.").First, ct);
            await page.WaitForTimeoutAsync(220).WaitAsync(ct);

            await SelectInspectorTabAndWaitAsync(page, "Signals", page.ChildNode(CreatorView.InspectorSignalsKey).First, ct);
            await page.WaitForTimeoutAsync(220).WaitAsync(ct);

            await SelectInspectorTabAndWaitAsync(page, "State Graph", page.SceneCameraHud().First, ct);
            await page.WaitForTimeoutAsync(220).WaitAsync(ct);

            await SelectInspectorTabAndWaitAsync(page, "Scenarios", page.KeyedNode("run-scenario").First, ct);
        }
        catch (Exception ex)
        {
            var visible = await page.EvaluateAsync<string>("() => document.querySelector('flt-semantics-host')?.innerText ?? '<no-semantics>'");
            var ids = await page.ReadSemanticsIdentifiersAsync(ct);
            throw new Xunit.Sdk.XunitException(
                $"Creator & Flame E2E failed: {ex.Message}\nHTTP errors: [{string.Join(", ", httpErrors)}]\nIdentifiers: [{string.Join(", ", ids.Take(80))}]\nVisible text:\n{visible[..Math.Min(1500, visible.Length)]}",
                ex);
        }

        Assert.Empty(httpErrors);
    }

    private static async Task<float> WaitForCanvasWidthChangeAsync(IPage page, float previousWidth, bool expectWider, CancellationToken ct)
    {
        for (var i = 0; i < 40; i++)
        {
            var box = await page.MeasureSceneCanvasBoxAsync("brain", ct);
            if (expectWider && box.Width > previousWidth + 100f)
            {
                return box.Width;
            }
            if (!expectWider && box.Width < previousWidth - 100f)
            {
                return box.Width;
            }
            await page.WaitForTimeoutAsync(100).WaitAsync(ct);
        }
        return (await page.MeasureSceneCanvasBoxAsync("brain", ct)).Width;
    }

    private static async Task<string> WaitForZoomChangeAsync(IPage page, string previousZoom, CancellationToken ct)
    {
        for (var i = 0; i < 20; i++)
        {
            var current = await page.ReadSceneZoomTextAsync(ct);
            if (!string.IsNullOrWhiteSpace(current) && !string.Equals(current, previousZoom, StringComparison.Ordinal))
            {
                return current;
            }
            await page.WaitForTimeoutAsync(100).WaitAsync(ct);
        }
        return await page.ReadSceneZoomTextAsync(ct);
    }

    private static async Task<string> WaitForZoomTargetAsync(IPage page, string expectedZoom, CancellationToken ct)
    {
        for (var i = 0; i < 20; i++)
        {
            var current = await page.ReadSceneZoomTextAsync(ct);
            if (string.Equals(current, expectedZoom, StringComparison.Ordinal))
            {
                return current;
            }
            await page.WaitForTimeoutAsync(100).WaitAsync(ct);
        }
        return await page.ReadSceneZoomTextAsync(ct);
    }

    private static async Task SelectInspectorTabAndWaitAsync(IPage page, string tabLabel, ILocator expectedTarget, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await SelectCreatorTabAsync(page, tabLabel, ct);
            try
            {
                await expectedTarget.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = attempt == 2 ? 15_000 : 4_000 }).WaitAsync(ct);
                return;
            }
            catch (TimeoutException) when (attempt < 2)
            {
                await page.WaitForTimeoutAsync(200).WaitAsync(ct);
            }
        }
    }

    // A tab of Creator's inspector tab strip child (control.tabStrip), which sends control.select through Creator.
    private static async Task SelectCreatorTabAsync(IPage page, string label, CancellationToken ct)
    {
        var strip = page.ChildNode(CreatorView.InspectorTabsKey).First;
        await strip.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 }).WaitAsync(ct);
        var byRole = strip.GetByRole(AriaRole.Tab, new() { Name = label, Exact = true }).First;
        var target = await byRole.CountAsync().WaitAsync(ct) > 0 ? byRole : strip.GetByText(label, new() { Exact = true }).First;
        await target.ClickAsync(new() { Timeout = 15_000, Force = true }).WaitAsync(ct);
    }

    private static async Task WaitForSemanticsTextContainsAsync(IPage page, string expectedSubstring, CancellationToken ct)
    {
        try
        {
            await page.WaitForFunctionAsync(
                "expected => (document.querySelector('flt-semantics-host')?.innerText ?? '').toLowerCase().includes(expected.toLowerCase())",
                expectedSubstring,
                new PageWaitForFunctionOptions { Timeout = 45_000 }).WaitAsync(ct);
            return;
        }
        catch (TimeoutException)
        {
        }

        var finalText = await page.EvaluateAsync<string>("() => document.querySelector('flt-semantics-host')?.innerText ?? ''").WaitAsync(ct);
        Assert.Contains(expectedSubstring, finalText, StringComparison.OrdinalIgnoreCase);
    }
}
