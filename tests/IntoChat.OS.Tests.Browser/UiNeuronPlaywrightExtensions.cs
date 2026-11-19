using Microsoft.Playwright;

namespace IntoChat.OS.Tests.Browser;

/// <summary>
/// Typed Playwright extension methods for driving any `IntoChat.OS.Modules.UI` neuron
/// in a real browser against Flutter Web's semantics DOM (`?semantics=true`).
/// Every `NeuronWidget` exposes `flt-semantics-identifier="neuron:{kind}:{key}"`.
/// </summary>
public static class UiNeuronPlaywrightExtensions
{
    private const float DefaultTimeoutMs = 15_000;

    /// <summary>Locates a live `Modules.UI` neuron widget by its kind (`ui.chart`, `ui.toggle`, etc.) and brain-local key.</summary>
    public static ILocator Neuron(this IPage page, string kind, string key)
        => page.Locator($"[flt-semantics-identifier='neuron:{kind}:{key}']");

    /// <summary>Locates a keyed layout/button node (`flt-semantics-identifier="key:{key}"`).</summary>
    public static ILocator KeyedNode(this IPage page, string key)
        => page.Locator($"[flt-semantics-identifier='key:{key}']");

    /// <summary>Clicks a keyed button node (resolving inner role='button' if wrapped).</summary>
    public static async Task ClickButtonAsync(this IPage page, string key, CancellationToken ct = default)
    {
        var scope = page.KeyedNode(key).First;
        await scope.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct);
        var btn = scope.Locator("xpath=descendant-or-self::*[@role='button']").First;
        var target = await btn.CountAsync() > 0 && await btn.IsVisibleAsync() ? btn : scope;
        await target.ClickAsync(new() { Timeout = DefaultTimeoutMs }).WaitAsync(ct);
    }

    /// <summary>Toggles an `IToggle` (`ui.toggle`) neuron in the browser.</summary>
    public static async Task ToggleAsync(this IPage page, string key, CancellationToken ct = default)
    {
        var host = page.Neuron("ui.toggle", key).First;
        await host.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct);
        var toggle = host.Locator("xpath=descendant-or-self::*[@role='switch' or @role='checkbox' or @role='button']").First;
        var target = await toggle.CountAsync() > 0 ? toggle : host;
        await target.ClickAsync(new() { Timeout = DefaultTimeoutMs }).WaitAsync(ct);
    }

    /// <summary>Toggles an `ICheckBox` (`ui.checkbox`) neuron in the browser.</summary>
    public static async Task ToggleCheckBoxAsync(this IPage page, string key, CancellationToken ct = default)
    {
        var host = page.Neuron("ui.checkbox", key).First;
        await host.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct);
        var box = host.Locator("xpath=descendant-or-self::*[@role='checkbox' or @role='button']").First;
        var target = await box.CountAsync() > 0 ? box : host;
        await target.ClickAsync(new() { Timeout = DefaultTimeoutMs }).WaitAsync(ct);
    }

    /// <summary>Expands or collapses an `IExpander` (`ui.expander`) neuron in the browser.</summary>
    public static async Task ToggleExpanderAsync(this IPage page, string key, CancellationToken ct = default)
    {
        var host = page.Neuron("ui.expander", key).First;
        await host.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct);
        var trigger = host.Locator("xpath=descendant-or-self::*[@role='button']").First;
        var target = await trigger.CountAsync() > 0 ? trigger : host;
        await target.ClickAsync(new() { Timeout = DefaultTimeoutMs }).WaitAsync(ct);
    }

    /// <summary>Selects a tab by visible label inside an `ITabs` (`ui.tabs`) neuron.</summary>
    public static async Task SelectTabAsync(this IPage page, string key, string label, CancellationToken ct = default)
    {
        var host = page.Neuron("ui.tabs", key).First;
        await host.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct);
        await host.GetByText(label, new() { Exact = true }).First.ClickAsync(new() { Timeout = DefaultTimeoutMs }).WaitAsync(ct);
    }

    /// <summary>Selects an item by visible label inside an `IListBox` (`ui.listbox`) neuron.</summary>
    public static async Task SelectListItemAsync(this IPage page, string key, string label, CancellationToken ct = default)
    {
        var host = page.Neuron("ui.listbox", key).First;
        await host.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct);
        await host.GetByText(label, new() { Exact = true }).First.ClickAsync(new() { Timeout = DefaultTimeoutMs }).WaitAsync(ct);
    }

    /// <summary>Clicks the chart-kind switch button (`Line` / `Columns`) inside an `IChart` (`ui.chart`) neuron.</summary>
    public static async Task SwitchChartKindAsync(this IPage page, string key, string kindButtonLabel, CancellationToken ct = default)
    {
        var host = page.Neuron("ui.chart", key).First;
        await host.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct);
        await host.GetByText(kindButtonLabel, new() { Exact = true }).First.ClickAsync(new() { Timeout = DefaultTimeoutMs }).WaitAsync(ct);
    }

    /// <summary>Types a filter query into an `ITable` (`ui.table`) neuron's filter box.</summary>
    public static async Task FilterTableAsync(this IPage page, string key, string filterText, CancellationToken ct = default)
    {
        var host = page.Neuron("ui.table", key).First;
        await host.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct);
        var input = host.Locator("input, textarea, [role='textbox']").First;
        await input.FillAsync(filterText, new() { Timeout = DefaultTimeoutMs }).WaitAsync(ct);
    }

    /// <summary>Clicks a column header inside an `ITable` (`ui.table`) neuron to toggle sorting.</summary>
    public static async Task SortTableAsync(this IPage page, string key, string columnLabel, CancellationToken ct = default)
    {
        var host = page.Neuron("ui.table", key).First;
        await host.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct);
        await host.GetByText(columnLabel, new() { Exact = true }).First.ClickAsync(new() { Timeout = DefaultTimeoutMs }).WaitAsync(ct);
    }

    /// <summary>Selects a radio option inside an `IRadioButtons` (`ui.radio-buttons`) neuron.</summary>
    public static async Task SelectRadioAsync(this IPage page, string key, string optionLabel, CancellationToken ct = default)
    {
        var host = page.Neuron("ui.radio-buttons", key).First;
        await host.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct);
        await host.GetByText(optionLabel, new() { Exact = true }).First.ClickAsync(new() { Timeout = DefaultTimeoutMs }).WaitAsync(ct);
    }

    /// <summary>Clicks a tree node inside an `ITreeView` (`ui.tree-view`) neuron.</summary>
    public static async Task SelectTreeNodeAsync(this IPage page, string key, string nodeLabel, CancellationToken ct = default)
    {
        var host = page.Neuron("ui.tree-view", key).First;
        await host.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct);
        await host.GetByText(nodeLabel, new() { Exact = true }).First.ClickAsync(new() { Timeout = DefaultTimeoutMs }).WaitAsync(ct);
    }

    /// <summary>Clicks a breadcrumb segment inside an `IBreadcrumbBar` (`ui.breadcrumb`) neuron.</summary>
    public static async Task ClickBreadcrumbAsync(this IPage page, string key, string segmentLabel, CancellationToken ct = default)
    {
        var host = page.Neuron("ui.breadcrumb", key).First;
        await host.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct);
        await host.GetByText(segmentLabel, new() { Exact = true }).First.ClickAsync(new() { Timeout = DefaultTimeoutMs }).WaitAsync(ct);
    }

    /// <summary>Dismisses an `IInfoBar` (`ui.info-bar`) neuron.</summary>
    public static async Task DismissInfoBarAsync(this IPage page, string key, CancellationToken ct = default)
    {
        var host = page.Neuron("ui.info-bar", key).First;
        await host.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct);
        await host.Locator("[role='button']").Last.ClickAsync(new() { Timeout = DefaultTimeoutMs }).WaitAsync(ct);
    }

    /// <summary>Reads all visible rendered text inside a `Modules.UI` neuron widget.</summary>
    public static async Task<string> ReadNeuronTextAsync(this IPage page, string kind, string key, CancellationToken ct = default)
    {
        var host = page.Neuron(kind, key).First;
        await host.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct);
        return (await host.InnerTextAsync(new() { Timeout = DefaultTimeoutMs }).WaitAsync(ct)).Trim();
    }
}
