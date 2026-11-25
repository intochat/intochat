using System.Globalization;
using System.Text.RegularExpressions;
using PlaywrightSdk = Microsoft.Playwright;

namespace IntoChat.OS.Modules.Microsoft.Playwright;

public sealed record UiGeometryBox(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;

    public float Bottom => Y + Height;

    public float HeightRatio(float viewportHeight) => viewportHeight > 0f ? Height / viewportHeight : 0f;

    public float WidthRatio(float viewportWidth) => viewportWidth > 0f ? Width / viewportWidth : 0f;

    public string Format(float viewportWidth = 1440f, float viewportHeight = 900f) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"x={X:0.#}, y={Y:0.#}, w={Width:0.#}, h={Height:0.#} ({WidthRatio(viewportWidth) * 100f:0.0}%w x {HeightRatio(viewportHeight) * 100f:0.0}%h)");
}

// Typed Playwright helpers driving IntoChat.OS.Modules.UI neurons, Flame SceneView camera HUD,
// Studio shell chrome, WebBrowser surfaces, Creator time-travel controls, and DOM bounding-box geometry.
public static partial class UiNeuronPlaywrightExtensions
{
    private const float DefaultTimeoutMs = 15_000;
    private static readonly PlaywrightSdk.LocatorClickOptions ClickOpts = new() { Timeout = DefaultTimeoutMs, Force = true };

    [GeneratedRegex(@"\b\d{1,3}%", RegexOptions.CultureInvariant)]
    private static partial Regex ZoomPercentPattern();

    public static PlaywrightSdk.ILocator Neuron(this PlaywrightSdk.IPage page, string kind, string key)
    {
        ArgumentNullException.ThrowIfNull(page);
        return page.Locator($"[flt-semantics-identifier='neuron:{kind}:{key}']");
    }

    public static PlaywrightSdk.ILocator KeyedNode(this PlaywrightSdk.IPage page, string key)
    {
        ArgumentNullException.ThrowIfNull(page);
        return page.Locator($"[flt-semantics-identifier='key:{key}']");
    }

    public static PlaywrightSdk.ILocator SlotNode(this PlaywrightSdk.IPage page, string slotPath)
    {
        ArgumentNullException.ThrowIfNull(page);
        return page.Locator($"[flt-semantics-identifier='slot:{slotPath}']");
    }

    public static PlaywrightSdk.ILocator SemanticsId(this PlaywrightSdk.IPage page, string identifier)
    {
        ArgumentNullException.ThrowIfNull(page);
        return page.Locator($"[flt-semantics-identifier='{identifier}']");
    }

    public static PlaywrightSdk.ILocator SemanticsPrefix(this PlaywrightSdk.IPage page, string prefix)
    {
        ArgumentNullException.ThrowIfNull(page);
        return page.Locator($"[flt-semantics-identifier^='{prefix}']");
    }

    public static async Task ClickButtonAsync(this PlaywrightSdk.IPage page, string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var scope = page.KeyedNode(key).First;
        await scope.WaitForAsync(new() { State = PlaywrightSdk.WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
        var btn = scope.Locator("xpath=descendant-or-self::*[@role='button']").First;
        var target = await btn.CountAsync().WaitAsync(ct).ConfigureAwait(false) > 0 && await btn.IsVisibleAsync().WaitAsync(ct).ConfigureAwait(false) ? btn : scope;
        await ClickStableAsync(target, ct).ConfigureAwait(false);
    }

    public static async Task SelectTabAsync(this PlaywrightSdk.IPage page, string key, string label, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var host = page.Neuron("ui.tabs", key).First;
        await host.WaitForAsync(new() { State = PlaywrightSdk.WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);

        var byRole = host.GetByRole(PlaywrightSdk.AriaRole.Button, new() { Name = label, Exact = true }).First;
        PlaywrightSdk.ILocator target;
        if (await byRole.CountAsync().WaitAsync(ct).ConfigureAwait(false) > 0)
        {
            target = byRole;
        }
        else
        {
            var exact = host.GetByText(label, new() { Exact = true }).First;
            target = await exact.CountAsync().WaitAsync(ct).ConfigureAwait(false) > 0
                ? exact
                : host.GetByText(label, new() { Exact = false }).First;
        }

        for (var settle = 0; settle < 3; settle++)
        {
            var b1 = await target.BoundingBoxAsync(new() { Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
            await page.WaitForTimeoutAsync(90).WaitAsync(ct).ConfigureAwait(false);
            var b2 = await target.BoundingBoxAsync(new() { Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
            if (b1 is not null && b2 is not null && Math.Abs(b1.X - b2.X) <= 1f && Math.Abs(b1.Y - b2.Y) <= 1f)
            {
                break;
            }
        }

        await ClickStableAsync(target, ct).ConfigureAwait(false);
    }

    public static async Task SwitchChartKindAsync(this PlaywrightSdk.IPage page, string key, string kindButtonLabel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var host = page.Neuron("ui.chart", key).First;
        await host.WaitForAsync(new() { State = PlaywrightSdk.WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
        await host.GetByText(kindButtonLabel, new() { Exact = true }).First.ClickAsync(ClickOpts).WaitAsync(ct).ConfigureAwait(false);
    }

    public static async Task FilterTableAsync(this PlaywrightSdk.IPage page, string key, string filterText, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var host = page.Neuron("ui.table", key).First;
        await host.WaitForAsync(new() { State = PlaywrightSdk.WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
        var input = host.Locator("input, textarea, [role='textbox']").First;
        await input.FillAsync(filterText, new() { Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
    }

    public static async Task SortTableAsync(this PlaywrightSdk.IPage page, string key, string columnLabel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var host = page.Neuron("ui.table", key).First;
        await host.WaitForAsync(new() { State = PlaywrightSdk.WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
        var btn = host.GetByRole(PlaywrightSdk.AriaRole.Button, new() { Name = columnLabel }).First;
        var target = await btn.CountAsync().WaitAsync(ct).ConfigureAwait(false) > 0 ? btn : host.GetByText(columnLabel, new() { Exact = false }).First;
        await target.ClickAsync(ClickOpts).WaitAsync(ct).ConfigureAwait(false);
    }

    public static async Task SelectTableRowAsync(this PlaywrightSdk.IPage page, string key, string rowText, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var host = page.Neuron("ui.table", key).First;
        await host.WaitForAsync(new() { State = PlaywrightSdk.WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
        await host.GetByText(rowText, new() { Exact = false }).First.ClickAsync(ClickOpts).WaitAsync(ct).ConfigureAwait(false);
    }

    public static async Task SelectTreeNodeAsync(this PlaywrightSdk.IPage page, string key, string nodeLabel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var host = page.Neuron("ui.tree-view", key).First;
        await host.WaitForAsync(new() { State = PlaywrightSdk.WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
        await host.GetByText(nodeLabel, new() { Exact = false }).First.ClickAsync(ClickOpts).WaitAsync(ct).ConfigureAwait(false);
    }

    public static async Task ClickBreadcrumbAsync(this PlaywrightSdk.IPage page, string key, string segmentLabel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var host = page.Neuron("ui.breadcrumb", key).First;
        await host.WaitForAsync(new() { State = PlaywrightSdk.WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
        await host.GetByText(segmentLabel, new() { Exact = false }).First.ClickAsync(ClickOpts).WaitAsync(ct).ConfigureAwait(false);
    }

    public static async Task SelectGridItemAsync(this PlaywrightSdk.IPage page, string key, string itemTitle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var host = page.Neuron("ui.grid-view", key).First;
        await host.WaitForAsync(new() { State = PlaywrightSdk.WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
        await host.GetByText(itemTitle, new() { Exact = false }).First.ClickAsync(ClickOpts).WaitAsync(ct).ConfigureAwait(false);
    }

    public static async Task ClickCommandBarItemAsync(this PlaywrightSdk.IPage page, string key, string itemLabel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var host = page.Neuron("ui.command-bar", key).First;
        await host.WaitForAsync(new() { State = PlaywrightSdk.WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
        var btn = host.GetByRole(PlaywrightSdk.AriaRole.Button, new() { Name = itemLabel }).First;
        var target = await btn.CountAsync().WaitAsync(ct).ConfigureAwait(false) > 0 ? btn : host.GetByText(itemLabel, new() { Exact = false }).First;
        await target.ClickAsync(ClickOpts).WaitAsync(ct).ConfigureAwait(false);
    }

    public static async Task SendChatMessageAsync(this PlaywrightSdk.IPage page, string key, string message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var host = page.Neuron("ui.chat", key).First;
        await host.WaitForAsync(new() { State = PlaywrightSdk.WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
        var input = host.Locator("input:not([disabled]):not([readonly]), textarea:not([disabled]):not([readonly])").Last;
        await input.ClickAsync(ClickOpts).WaitAsync(ct).ConfigureAwait(false);
        await page.WaitForTimeoutAsync(50).WaitAsync(ct).ConfigureAwait(false);
        await input.FillAsync(message, new() { Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
        var sendRole = host.GetByRole(PlaywrightSdk.AriaRole.Button, new() { Name = "Send", Exact = true }).First;
        if (await sendRole.CountAsync().WaitAsync(ct).ConfigureAwait(false) > 0)
        {
            await ClickStableAsync(sendRole, ct).ConfigureAwait(false);
            return;
        }
        var sendText = host.GetByText("Send", new() { Exact = true }).First;
        if (await sendText.CountAsync().WaitAsync(ct).ConfigureAwait(false) > 0)
        {
            await ClickStableAsync(sendText, ct).ConfigureAwait(false);
            return;
        }
        await input.PressAsync("Enter", new() { Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
    }

    // Wave 3: IStepper (ui.stepper)
    public static PlaywrightSdk.ILocator Stepper(this PlaywrightSdk.IPage page, string key)
        => page.Neuron("ui.stepper", key);

    public static PlaywrightSdk.ILocator StepperStep(this PlaywrightSdk.IPage page, string stepId)
        => page.SemanticsId($"stepper-step:{stepId}");

    public static async Task SelectStepAsync(this PlaywrightSdk.IPage page, string key, string stepLabel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var host = page.Stepper(key).First;
        await host.WaitForAsync(new() { State = PlaywrightSdk.WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
        await host.GetByText(stepLabel, new() { Exact = false }).First.ClickAsync(ClickOpts).WaitAsync(ct).ConfigureAwait(false);
    }

    public static async Task AdvanceStepperAsync(this PlaywrightSdk.IPage page, string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var advanceScope = page.SemanticsId($"stepper-advance:{key}").First;
        if (await advanceScope.CountAsync().WaitAsync(ct).ConfigureAwait(false) > 0)
        {
            var btn = advanceScope.Locator("xpath=descendant-or-self::*[@role='button']").First;
            var target = await btn.CountAsync().WaitAsync(ct).ConfigureAwait(false) > 0 ? btn : advanceScope;
            await target.ClickAsync(ClickOpts).WaitAsync(ct).ConfigureAwait(false);
            return;
        }
        var host = page.Stepper(key).First;
        await host.WaitForAsync(new() { State = PlaywrightSdk.WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
        await host.GetByText("Advance", new() { Exact = false }).First.ClickAsync(ClickOpts).WaitAsync(ct).ConfigureAwait(false);
    }

    public static Task<string> ReadStepperTextAsync(this PlaywrightSdk.IPage page, string key, CancellationToken ct = default)
        => page.ReadNeuronTextAsync("ui.stepper", key, ct);

    // Wave 3: IPropertyGrid (ui.property-grid)
    public static PlaywrightSdk.ILocator PropertyGrid(this PlaywrightSdk.IPage page, string key)
        => page.Neuron("ui.property-grid", key);

    public static async Task FilterPropertyGridAsync(this PlaywrightSdk.IPage page, string key, string filterText, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var host = page.PropertyGrid(key).First;
        await host.WaitForAsync(new() { State = PlaywrightSdk.WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
        var input = host.Locator("input, textarea, [role='textbox']").First;
        await input.FillAsync(filterText, new() { Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
    }

    public static async Task TogglePropertyNodeAsync(this PlaywrightSdk.IPage page, string key, string propertyLabel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var host = page.PropertyGrid(key).First;
        await host.WaitForAsync(new() { State = PlaywrightSdk.WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
        await host.GetByText(propertyLabel, new() { Exact = false }).First.ClickAsync(ClickOpts).WaitAsync(ct).ConfigureAwait(false);
    }

    public static Task<string> ReadPropertyGridTextAsync(this PlaywrightSdk.IPage page, string key, CancellationToken ct = default)
        => page.ReadNeuronTextAsync("ui.property-grid", key, ct);

    // Wave 3: IKanbanBoard (ui.kanban)
    public static PlaywrightSdk.ILocator KanbanBoard(this PlaywrightSdk.IPage page, string key)
        => page.Neuron("ui.kanban", key);

    public static PlaywrightSdk.ILocator KanbanColumn(this PlaywrightSdk.IPage page, string columnId)
        => page.SemanticsId($"kanban-col:{columnId}");

    public static PlaywrightSdk.ILocator KanbanCard(this PlaywrightSdk.IPage page, string cardId)
        => page.SemanticsId($"kanban-card:{cardId}");

    public static async Task ClickKanbanCardAsync(this PlaywrightSdk.IPage page, string key, string cardTitle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var host = page.KanbanBoard(key).First;
        await host.WaitForAsync(new() { State = PlaywrightSdk.WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
        await host.GetByText(cardTitle, new() { Exact = false }).First.ClickAsync(ClickOpts).WaitAsync(ct).ConfigureAwait(false);
    }

    public static async Task MoveKanbanCardRightAsync(this PlaywrightSdk.IPage page, string cardId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var card = page.KanbanCard(cardId).First;
        await card.WaitForAsync(new() { State = PlaywrightSdk.WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
        var moveBtn = card.GetByRole(PlaywrightSdk.AriaRole.Button, new() { Name = "Move right" }).First;
        var target = await moveBtn.CountAsync().WaitAsync(ct).ConfigureAwait(false) > 0
            ? moveBtn
            : card.Locator("[aria-label='Move right']").First;
        await target.ClickAsync(ClickOpts).WaitAsync(ct).ConfigureAwait(false);
    }

    public static async Task MoveKanbanCardLeftAsync(this PlaywrightSdk.IPage page, string cardId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var card = page.KanbanCard(cardId).First;
        await card.WaitForAsync(new() { State = PlaywrightSdk.WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
        var moveBtn = card.GetByRole(PlaywrightSdk.AriaRole.Button, new() { Name = "Move left" }).First;
        var target = await moveBtn.CountAsync().WaitAsync(ct).ConfigureAwait(false) > 0
            ? moveBtn
            : card.Locator("[aria-label='Move left']").First;
        await target.ClickAsync(ClickOpts).WaitAsync(ct).ConfigureAwait(false);
    }

    public static async Task MoveKanbanCardAsync(this PlaywrightSdk.IPage page, string key, string cardTitle, string actionOrColumnLabel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var host = page.KanbanBoard(key).First;
        await host.WaitForAsync(new() { State = PlaywrightSdk.WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
        var card = host.GetByText(cardTitle, new() { Exact = false }).First;
        await card.ClickAsync(ClickOpts).WaitAsync(ct).ConfigureAwait(false);
        await page.WaitForTimeoutAsync(200).WaitAsync(ct).ConfigureAwait(false);
        var target = host.GetByText(actionOrColumnLabel, new() { Exact = false }).First;
        await target.ClickAsync(ClickOpts).WaitAsync(ct).ConfigureAwait(false);
    }

    public static Task<string> ReadKanbanTextAsync(this PlaywrightSdk.IPage page, string key, CancellationToken ct = default)
        => page.ReadNeuronTextAsync("ui.kanban", key, ct);

    public static Task<string> ReadKanbanColumnTextAsync(this PlaywrightSdk.IPage page, string columnId, CancellationToken ct = default)
        => page.ReadSemanticsSubtreeTextAsync($"kanban-col:{columnId}", ct);

    // CodeEditor (ui.code-editor) + re_editor / re_highlight syntax inspection
    public static PlaywrightSdk.ILocator CodeEditor(this PlaywrightSdk.IPage page, string key)
        => page.Neuron("ui.code-editor", key);

    public static Task<string> ReadCodeEditorTextAsync(this PlaywrightSdk.IPage page, string key, CancellationToken ct = default)
        => page.ReadNeuronTextAsync("ui.code-editor", key, ct);

    public static async Task<string[]> ReadCodeEditorSemanticsAsync(this PlaywrightSdk.IPage page, string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var host = page.CodeEditor(key).First;
        await host.WaitForAsync(new() { State = PlaywrightSdk.WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
        return await host.EvaluateAsync<string[]>(
            """
            el => {
              const items = [];
              const push = v => { v = (v || '').trim(); if (v) items.push(v); };
              const walk = node => {
                if (node.nodeType === Node.ELEMENT_NODE) {
                  push(node.getAttribute('flt-semantics-identifier'));
                  push(node.getAttribute('aria-label'));
                  if (node instanceof HTMLInputElement || node instanceof HTMLTextAreaElement) push(node.value);
                } else if (node.nodeType === Node.TEXT_NODE) {
                  push(node.textContent);
                }
                for (const c of node.childNodes) walk(c);
              };
              walk(el);
              return items;
            }
            """).WaitAsync(ct).ConfigureAwait(false);
    }

    // Flame SceneView Camera HUD (zoom in, zoom out, fit, reset, follow live, minimap, zoom readout, pan/wheel gestures)
    public static PlaywrightSdk.ILocator SceneCanvas(this PlaywrightSdk.IPage page, string sceneKey = "brain")
        => page.SemanticsId($"scene:{sceneKey}");

    public static PlaywrightSdk.ILocator SceneCameraHud(this PlaywrightSdk.IPage page)
        => page.KeyedNode("flame-camera-hud");

    public static PlaywrightSdk.ILocator SceneFollowLive(this PlaywrightSdk.IPage page)
        => page.KeyedNode("flame-follow-live");

    public static PlaywrightSdk.ILocator SceneViewportMinimap(this PlaywrightSdk.IPage page)
        => page.KeyedNode("flame-viewport-minimap");

    public static async Task ToggleSceneFollowLiveAsync(this PlaywrightSdk.IPage page, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var target = await ResolveSceneHudButtonAsync(
            page,
            ["key:flame-follow-live", "flame-follow-live"],
            ["Follow live", "Follow Live", "Live"],
            ct).ConfigureAwait(false);
        await target.ClickAsync(ClickOpts).WaitAsync(ct).ConfigureAwait(false);
    }

    public static async Task ClickSceneMinimapAsync(
        this PlaywrightSdk.IPage page,
        float normalizedX = 0.5f,
        float normalizedY = 0.5f,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var minimap = page.SceneViewportMinimap().First;
        var box = await minimap.WaitForBoxAsync(8f, 8f, ct).ConfigureAwait(false);
        if (box.Width > 1f && box.Height > 1f)
        {
            var clampedX = Math.Clamp(normalizedX, 0.05f, 0.95f);
            var clampedY = Math.Clamp(normalizedY, 0.05f, 0.95f);
            await page.Mouse.ClickAsync(box.X + (box.Width * clampedX), box.Y + (box.Height * clampedY)).WaitAsync(ct).ConfigureAwait(false);
            return;
        }
        await minimap.ClickAsync(ClickOpts).WaitAsync(ct).ConfigureAwait(false);
    }

    // Studio AppShell chrome toggles + WebBrowser & real DOM BoundingBoxAsync geometry probes
    public static PlaywrightSdk.ILocator ShellToggleSidebar(this PlaywrightSdk.IPage page)
        => page.KeyedNode("shell-toggle-sidebar");

    public static PlaywrightSdk.ILocator ShellToggleInspector(this PlaywrightSdk.IPage page)
        => page.KeyedNode("shell-toggle-inspector");

    public static Task ToggleShellSidebarAsync(this PlaywrightSdk.IPage page, CancellationToken ct = default)
        => page.ClickButtonAsync("shell-toggle-sidebar", ct);

    public static Task ToggleShellInspectorAsync(this PlaywrightSdk.IPage page, CancellationToken ct = default)
        => page.ClickButtonAsync("shell-toggle-inspector", ct);

    public static PlaywrightSdk.ILocator WebBrowserSurface(this PlaywrightSdk.IPage page, string key)
        => page.KeyedNode(key);

    public static Task<UiGeometryBox> MeasureWebBrowserBoxAsync(
        this PlaywrightSdk.IPage page,
        string key,
        CancellationToken ct = default)
        => page.WebBrowserSurface(key).WaitForBoxAsync(10f, 10f, ct);

    public static async Task<UiGeometryBox?> MeasureBoxAsync(
        this PlaywrightSdk.ILocator locator,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(locator);
        var first = locator.First;
        if (await first.CountAsync().WaitAsync(ct).ConfigureAwait(false) == 0)
        {
            return null;
        }

        var direct = await first.BoundingBoxAsync(new() { Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
        if (direct is { Width: > 1f, Height: > 1f })
        {
            return new UiGeometryBox(direct.X, direct.Y, direct.Width, direct.Height);
        }

        var union = await first.EvaluateAsync<float[]?>(
            """
            el => {
              const r = el.getBoundingClientRect();
              if (r.width > 1 && r.height > 1) return [r.x, r.y, r.width, r.height];
              let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
              for (const d of el.querySelectorAll('*')) {
                const dr = d.getBoundingClientRect();
                if (dr.width > 1 && dr.height > 1) {
                  if (dr.left < minX) minX = dr.left;
                  if (dr.top < minY) minY = dr.top;
                  if (dr.right > maxX) maxX = dr.right;
                  if (dr.bottom > maxY) maxY = dr.bottom;
                }
              }
              return (minX < maxX && minY < maxY) ? [minX, minY, maxX - minX, maxY - minY] : null;
            }
            """).WaitAsync(ct).ConfigureAwait(false);

        return union is { Length: 4 }
            ? new UiGeometryBox(union[0], union[1], union[2], union[3])
            : direct is not null ? new UiGeometryBox(direct.X, direct.Y, direct.Width, direct.Height) : null;
    }

    public static async Task<UiGeometryBox> WaitForBoxAsync(
        this PlaywrightSdk.ILocator locator,
        float minWidth = 1f,
        float minHeight = 1f,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(locator);
        await locator.First.WaitForAsync(new() { State = PlaywrightSdk.WaitForSelectorState.Attached, Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
        UiGeometryBox? latest = null;
        for (var i = 0; i < 60; i++)
        {
            latest = await locator.MeasureBoxAsync(ct).ConfigureAwait(false);
            if (latest is { } b && b.Width >= minWidth && b.Height >= minHeight)
            {
                return b;
            }
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        return latest ?? new UiGeometryBox(0f, 0f, 0f, 0f);
    }

    public static Task<UiGeometryBox> MeasureKeyedBoxAsync(
        this PlaywrightSdk.IPage page,
        string key,
        float minWidth = 1f,
        float minHeight = 1f,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        return page.KeyedNode(key).WaitForBoxAsync(minWidth, minHeight, ct);
    }

    public static Task<UiGeometryBox> MeasureNeuronBoxAsync(
        this PlaywrightSdk.IPage page,
        string kind,
        string key,
        float minWidth = 1f,
        float minHeight = 1f,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        return page.Neuron(kind, key).WaitForBoxAsync(minWidth, minHeight, ct);
    }

    public static async Task<UiGeometryBox> MeasureSceneCanvasBoxAsync(
        this PlaywrightSdk.IPage page,
        string sceneKey = "brain",
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var named = page.SceneCanvas(sceneKey);
        if (await named.CountAsync().WaitAsync(ct).ConfigureAwait(false) > 0)
        {
            return await named.WaitForBoxAsync(100f, 100f, ct).ConfigureAwait(false);
        }
        return await page.SemanticsPrefix("scene:").WaitForBoxAsync(100f, 100f, ct).ConfigureAwait(false);
    }

    public static async Task ZoomSceneInAsync(this PlaywrightSdk.IPage page, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var target = await ResolveSceneHudButtonAsync(
            page,
            ["key:flame-zoom-in", "flame-zoom-in", "scene-camera-zoom-in", "camera-zoom-in", "key:camera-zoom-in"],
            ["Zoom in", "+"],
            ct).ConfigureAwait(false);
        await target.ClickAsync(ClickOpts).WaitAsync(ct).ConfigureAwait(false);
    }

    public static async Task ZoomSceneOutAsync(this PlaywrightSdk.IPage page, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var target = await ResolveSceneHudButtonAsync(
            page,
            ["key:flame-zoom-out", "flame-zoom-out", "scene-camera-zoom-out", "camera-zoom-out", "key:camera-zoom-out"],
            ["Zoom out", "-", "−"],
            ct).ConfigureAwait(false);
        await target.ClickAsync(ClickOpts).WaitAsync(ct).ConfigureAwait(false);
    }

    public static async Task FitSceneCameraAsync(this PlaywrightSdk.IPage page, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var target = await ResolveSceneHudButtonAsync(
            page,
            ["key:flame-fit", "flame-fit", "scene-camera-fit", "camera-fit", "key:camera-fit"],
            ["Fit to graph", "Fit"],
            ct).ConfigureAwait(false);
        await target.ClickAsync(ClickOpts).WaitAsync(ct).ConfigureAwait(false);
    }

    public static async Task ResetSceneCameraAsync(this PlaywrightSdk.IPage page, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var target = await ResolveSceneHudButtonAsync(
            page,
            ["key:flame-reset", "flame-reset", "scene-camera-reset", "camera-reset", "key:camera-reset"],
            ["Reset camera", "Reset"],
            ct).ConfigureAwait(false);
        await target.ClickAsync(ClickOpts).WaitAsync(ct).ConfigureAwait(false);
    }

    public static async Task<string> ReadSceneZoomTextAsync(this PlaywrightSdk.IPage page, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        foreach (var id in new[] { "key:flame-zoom-readout", "flame-zoom-readout", "key:flame-camera-hud", "scene-camera-zoom", "camera-zoom" })
        {
            var loc = page.SemanticsId(id).First;
            if (await loc.CountAsync().WaitAsync(ct).ConfigureAwait(false) > 0)
            {
                var aria = await loc.GetAttributeAsync("aria-label", new() { Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(aria))
                {
                    var ariaMatch = ZoomPercentPattern().Match(aria);
                    return ariaMatch.Success ? ariaMatch.Value : aria.Trim();
                }
                var txt = (await loc.InnerTextAsync(new() { Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false)).Trim();
                if (txt.Length > 0)
                {
                    var txtMatch = ZoomPercentPattern().Match(txt);
                    return txtMatch.Success ? txtMatch.Value : txt;
                }
            }
        }
        return await page.EvaluateAsync<string>(
            """
            () => {
              const host = document.querySelector('flt-semantics-host');
              if (!host) return '';
              const text = host.innerText || '';
              const m = text.match(/\b\d{1,3}%/);
              return m ? m[0] : '';
            }
            """).WaitAsync(ct).ConfigureAwait(false);
    }

    public static async Task PanSceneByDragAsync(this PlaywrightSdk.IPage page, float deltaX, float deltaY, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var size = page.ViewportSize;
        var startX = (size?.Width ?? 1600) * 0.5f;
        var startY = (size?.Height ?? 1000) * 0.5f;
        await page.Mouse.MoveAsync(startX, startY).WaitAsync(ct).ConfigureAwait(false);
        await page.Mouse.DownAsync().WaitAsync(ct).ConfigureAwait(false);
        await page.Mouse.MoveAsync(startX + deltaX, startY + deltaY, new() { Steps = 6 }).WaitAsync(ct).ConfigureAwait(false);
        await page.Mouse.UpAsync().WaitAsync(ct).ConfigureAwait(false);
    }

    public static async Task WheelZoomSceneAsync(this PlaywrightSdk.IPage page, float deltaY, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var size = page.ViewportSize;
        await page.Mouse.MoveAsync((size?.Width ?? 1600) * 0.5f, (size?.Height ?? 1000) * 0.5f).WaitAsync(ct).ConfigureAwait(false);
        await page.Mouse.WheelAsync(0, deltaY).WaitAsync(ct).ConfigureAwait(false);
    }

    // Creator Time-Travel keyboard stepping (ArrowLeft, ArrowRight, Space, Home, End)
    public static async Task StepTimeTravelBackwardAsync(this PlaywrightSdk.IPage page, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        await page.Keyboard.PressAsync("ArrowLeft").WaitAsync(ct).ConfigureAwait(false);
    }

    public static async Task StepTimeTravelForwardAsync(this PlaywrightSdk.IPage page, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        await page.Keyboard.PressAsync("ArrowRight").WaitAsync(ct).ConfigureAwait(false);
    }

    public static async Task ToggleTimeTravelModeAsync(this PlaywrightSdk.IPage page, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        await page.Keyboard.PressAsync("Space").WaitAsync(ct).ConfigureAwait(false);
    }

    public static async Task JumpTimeTravelFirstAsync(this PlaywrightSdk.IPage page, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        await page.Keyboard.PressAsync("Home").WaitAsync(ct).ConfigureAwait(false);
    }

    public static async Task JumpTimeTravelLastAsync(this PlaywrightSdk.IPage page, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        await page.Keyboard.PressAsync("End").WaitAsync(ct).ConfigureAwait(false);
    }

    public static Task ClickReplayControlAsync(this PlaywrightSdk.IPage page, string replayButtonKey, CancellationToken ct = default)
        => page.ClickButtonAsync(replayButtonKey, ct);

    public static async Task<string> ReadReplayCursorTextAsync(this PlaywrightSdk.IPage page, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var scope = page.KeyedNode("replay-cursor-readout").First;
        if (await scope.CountAsync().WaitAsync(ct).ConfigureAwait(false) > 0)
        {
            var txt = (await scope.InnerTextAsync(new() { Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false)).Trim();
            if (txt.Length > 0)
            {
                return txt;
            }
            var aria = await scope.GetAttributeAsync("aria-label", new() { Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(aria))
            {
                return aria.Trim();
            }
        }
        return string.Empty;
    }

    public static Task<string> ReadNeuronTextAsync(this PlaywrightSdk.IPage page, string kind, string key, CancellationToken ct = default)
        => page.ReadSemanticsSubtreeTextAsync($"neuron:{kind}:{key}", ct);

    public static async Task<string> ReadSemanticsSubtreeTextAsync(this PlaywrightSdk.IPage page, string identifier, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var host = page.SemanticsId(identifier).First;
        await host.WaitForAsync(new() { State = PlaywrightSdk.WaitForSelectorState.Attached, Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
        return await host.EvaluateAsync<string>(
            """
            el => {
              const items = [];
              const seen = new Set();
              const push = v => {
                v = (v || '').trim();
                if (v && !seen.has(v)) {
                  seen.add(v);
                  items.push(v);
                }
              };
              const walk = node => {
                if (node.nodeType === Node.ELEMENT_NODE) {
                  push(node.getAttribute('aria-label'));
                  if (node instanceof HTMLInputElement || node instanceof HTMLTextAreaElement) push(node.value);
                } else if (node.nodeType === Node.TEXT_NODE) {
                  push(node.textContent);
                }
                for (const c of node.childNodes) walk(c);
              };
              walk(el);
              return items.join('\n');
            }
            """).WaitAsync(ct).ConfigureAwait(false);
    }

    public static async Task<string[]> ReadSemanticsIdentifiersAsync(this PlaywrightSdk.IPage page, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        return await page.EvaluateAsync<string[]>(
            "() => Array.from(document.querySelectorAll('[flt-semantics-identifier]')).map(e => e.getAttribute('flt-semantics-identifier') || '').filter(Boolean)")
            .WaitAsync(ct)
            .ConfigureAwait(false);
    }

    private static async Task<PlaywrightSdk.ILocator> ResolveSceneHudButtonAsync(
        PlaywrightSdk.IPage page,
        string[] candidateIds,
        string[] candidateLabels,
        CancellationToken ct)
    {
        foreach (var id in candidateIds)
        {
            var scope = page.SemanticsId(id).First;
            if (await scope.CountAsync().WaitAsync(ct).ConfigureAwait(false) > 0)
            {
                var innerBtn = scope.Locator("xpath=descendant-or-self::*[@role='button']").First;
                return await innerBtn.CountAsync().WaitAsync(ct).ConfigureAwait(false) > 0 ? innerBtn : scope;
            }
        }
        foreach (var label in candidateLabels)
        {
            var byRole = page.GetByRole(PlaywrightSdk.AriaRole.Button, new() { Name = label, Exact = true }).First;
            if (await byRole.CountAsync().WaitAsync(ct).ConfigureAwait(false) > 0)
            {
                return byRole;
            }
        }
        return page.GetByText(candidateLabels[^1], new() { Exact = true }).First;
    }

    private static async Task ClickStableAsync(PlaywrightSdk.ILocator target, CancellationToken ct)
    {
        try
        {
            await target.ClickAsync(new() { Timeout = 4_000 }).WaitAsync(ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await target.ClickAsync(ClickOpts).WaitAsync(ct).ConfigureAwait(false);
        }
        catch (PlaywrightSdk.PlaywrightException)
        {
            await target.ClickAsync(ClickOpts).WaitAsync(ct).ConfigureAwait(false);
        }
    }
}
