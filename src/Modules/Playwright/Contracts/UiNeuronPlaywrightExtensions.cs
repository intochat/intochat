using System.Globalization;
using System.Text.RegularExpressions;
using PlaywrightSdk = Microsoft.Playwright;

namespace IntoChat.OS.Modules.Microsoft.Playwright;

// Typed Playwright helpers driving IntoChat.OS.Modules.UI neurons, Flame SceneView camera HUD,
// Creator time-travel controls, and re_editor code views over Flutter Web's semantics DOM (?semantics=true).
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
        await target.ClickAsync(ClickOpts).WaitAsync(ct).ConfigureAwait(false);
    }

    public static async Task ToggleAsync(this PlaywrightSdk.IPage page, string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var host = page.Neuron("ui.toggle", key).First;
        await host.WaitForAsync(new() { State = PlaywrightSdk.WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
        var toggle = host.Locator("xpath=descendant-or-self::*[@role='switch' or @role='checkbox' or @role='button']").First;
        var target = await toggle.CountAsync().WaitAsync(ct).ConfigureAwait(false) > 0 ? toggle : host;
        await target.ClickAsync(ClickOpts).WaitAsync(ct).ConfigureAwait(false);
    }

    public static async Task ToggleCheckBoxAsync(this PlaywrightSdk.IPage page, string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var host = page.Neuron("ui.checkbox", key).First;
        await host.WaitForAsync(new() { State = PlaywrightSdk.WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
        var box = host.Locator("xpath=descendant-or-self::*[@role='checkbox' or @role='button']").First;
        var target = await box.CountAsync().WaitAsync(ct).ConfigureAwait(false) > 0 ? box : host;
        await target.ClickAsync(ClickOpts).WaitAsync(ct).ConfigureAwait(false);
    }

    public static async Task ToggleExpanderAsync(this PlaywrightSdk.IPage page, string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var host = page.Neuron("ui.expander", key).First;
        await host.WaitForAsync(new() { State = PlaywrightSdk.WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
        var trigger = host.Locator("xpath=descendant-or-self::*[@role='button']").First;
        var target = await trigger.CountAsync().WaitAsync(ct).ConfigureAwait(false) > 0 ? trigger : host;
        await target.ClickAsync(ClickOpts).WaitAsync(ct).ConfigureAwait(false);
    }

    public static async Task SelectTabAsync(this PlaywrightSdk.IPage page, string key, string label, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var host = page.Neuron("ui.tabs", key).First;
        await host.WaitForAsync(new() { State = PlaywrightSdk.WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
        var exact = host.GetByText(label, new() { Exact = true }).First;
        var target = await exact.CountAsync().WaitAsync(ct).ConfigureAwait(false) > 0
            ? exact
            : host.GetByText(label, new() { Exact = false }).First;
        await target.ClickAsync(ClickOpts).WaitAsync(ct).ConfigureAwait(false);
    }

    public static async Task SelectListItemAsync(this PlaywrightSdk.IPage page, string key, string label, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var host = page.Neuron("ui.listbox", key).First;
        await host.WaitForAsync(new() { State = PlaywrightSdk.WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
        await host.GetByText(label, new() { Exact = false }).First.ClickAsync(ClickOpts).WaitAsync(ct).ConfigureAwait(false);
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

    public static async Task SelectRadioAsync(this PlaywrightSdk.IPage page, string key, string optionLabel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var host = page.Neuron("ui.radio-buttons", key).First;
        await host.WaitForAsync(new() { State = PlaywrightSdk.WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
        await host.GetByText(optionLabel, new() { Exact = false }).First.ClickAsync(ClickOpts).WaitAsync(ct).ConfigureAwait(false);
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

    public static async Task DismissInfoBarAsync(this PlaywrightSdk.IPage page, string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var host = page.Neuron("ui.info-bar", key).First;
        await host.WaitForAsync(new() { State = PlaywrightSdk.WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
        await host.Locator("[role='button']").Last.ClickAsync(ClickOpts).WaitAsync(ct).ConfigureAwait(false);
    }

    public static async Task ToggleCalendarDayAsync(this PlaywrightSdk.IPage page, string key, int day, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var host = page.Neuron("ui.calendar", key).First;
        await host.WaitForAsync(new() { State = PlaywrightSdk.WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
        await host.GetByText(day.ToString(CultureInfo.InvariantCulture), new() { Exact = true }).First.ClickAsync(ClickOpts).WaitAsync(ct).ConfigureAwait(false);
    }

    public static async Task SelectGridItemAsync(this PlaywrightSdk.IPage page, string key, string itemTitle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var host = page.Neuron("ui.grid-view", key).First;
        await host.WaitForAsync(new() { State = PlaywrightSdk.WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
        await host.GetByText(itemTitle, new() { Exact = false }).First.ClickAsync(ClickOpts).WaitAsync(ct).ConfigureAwait(false);
    }

    public static async Task ToggleMediaPlaybackAsync(this PlaywrightSdk.IPage page, string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var host = page.Neuron("ui.media-player", key).First;
        await host.WaitForAsync(new() { State = PlaywrightSdk.WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
        await host.Locator("[role='button']").First.ClickAsync(ClickOpts).WaitAsync(ct).ConfigureAwait(false);
    }

    public static async Task FillAutoSuggestAsync(this PlaywrightSdk.IPage page, string key, string query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var host = page.Neuron("ui.auto-suggest", key).First;
        await host.WaitForAsync(new() { State = PlaywrightSdk.WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
        var input = host.Locator("input, textarea, [role='textbox']").First;
        await input.FillAsync(query, new() { Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
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
        await input.PressSequentiallyAsync(message, new() { Delay = 5, Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
        var sendRole = host.GetByRole(PlaywrightSdk.AriaRole.Button, new() { Name = "Send", Exact = true }).First;
        if (await sendRole.CountAsync().WaitAsync(ct).ConfigureAwait(false) > 0)
        {
            await sendRole.ClickAsync(ClickOpts).WaitAsync(ct).ConfigureAwait(false);
            return;
        }
        var sendText = host.GetByText("Send", new() { Exact = true }).First;
        if (await sendText.CountAsync().WaitAsync(ct).ConfigureAwait(false) > 0)
        {
            await sendText.ClickAsync(ClickOpts).WaitAsync(ct).ConfigureAwait(false);
            return;
        }
        await input.PressAsync("Enter", new() { Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
    }

    // Wave 3: IStepper (ui.stepper)
    public static PlaywrightSdk.ILocator Stepper(this PlaywrightSdk.IPage page, string key)
        => page.Neuron("ui.stepper", key);

    public static async Task SelectStepAsync(this PlaywrightSdk.IPage page, string key, string stepLabel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var host = page.Stepper(key).First;
        await host.WaitForAsync(new() { State = PlaywrightSdk.WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
        await host.GetByText(stepLabel, new() { Exact = false }).First.ClickAsync(ClickOpts).WaitAsync(ct).ConfigureAwait(false);
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

    // Wave 3: IPipsPager (ui.pips-pager)
    public static PlaywrightSdk.ILocator PipsPager(this PlaywrightSdk.IPage page, string key)
        => page.Neuron("ui.pips-pager", key);

    public static async Task SelectPipPageAsync(this PlaywrightSdk.IPage page, string key, int pageIndex, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var host = page.PipsPager(key).First;
        await host.WaitForAsync(new() { State = PlaywrightSdk.WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
        var buttons = host.Locator("[role='button']");
        var count = await buttons.CountAsync().WaitAsync(ct).ConfigureAwait(false);
        var clamped = Math.Clamp(pageIndex, 0, Math.Max(0, count - 1));
        await buttons.Nth(clamped).ClickAsync(ClickOpts).WaitAsync(ct).ConfigureAwait(false);
    }

    public static async Task NextPipPageAsync(this PlaywrightSdk.IPage page, string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var host = page.PipsPager(key).First;
        await host.WaitForAsync(new() { State = PlaywrightSdk.WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
        await host.Locator("[role='button']").Last.ClickAsync(ClickOpts).WaitAsync(ct).ConfigureAwait(false);
    }

    public static async Task PreviousPipPageAsync(this PlaywrightSdk.IPage page, string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var host = page.PipsPager(key).First;
        await host.WaitForAsync(new() { State = PlaywrightSdk.WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
        await host.Locator("[role='button']").First.ClickAsync(ClickOpts).WaitAsync(ct).ConfigureAwait(false);
    }

    // Wave 3: IInfoBadge (ui.info-badge)
    public static PlaywrightSdk.ILocator InfoBadge(this PlaywrightSdk.IPage page, string key)
        => page.Neuron("ui.info-badge", key);

    public static Task<string> ReadInfoBadgeTextAsync(this PlaywrightSdk.IPage page, string key, CancellationToken ct = default)
        => page.ReadNeuronTextAsync("ui.info-badge", key, ct);

    // Wave 3: INotificationToast (ui.toast)
    public static PlaywrightSdk.ILocator Toast(this PlaywrightSdk.IPage page, string key)
        => page.Neuron("ui.toast", key);

    public static async Task ClickToastActionAsync(this PlaywrightSdk.IPage page, string key, string actionLabel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var host = page.Toast(key).First;
        await host.WaitForAsync(new() { State = PlaywrightSdk.WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
        var btn = host.GetByRole(PlaywrightSdk.AriaRole.Button, new() { Name = actionLabel }).First;
        var target = await btn.CountAsync().WaitAsync(ct).ConfigureAwait(false) > 0 ? btn : host.GetByText(actionLabel, new() { Exact = false }).First;
        await target.ClickAsync(ClickOpts).WaitAsync(ct).ConfigureAwait(false);
    }

    public static async Task DismissToastAsync(this PlaywrightSdk.IPage page, string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var host = page.Toast(key).First;
        await host.WaitForAsync(new() { State = PlaywrightSdk.WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
        await host.Locator("[role='button']").Last.ClickAsync(ClickOpts).WaitAsync(ct).ConfigureAwait(false);
    }

    // Wave 3: IFilePicker (ui.file-picker)
    public static PlaywrightSdk.ILocator FilePicker(this PlaywrightSdk.IPage page, string key)
        => page.Neuron("ui.file-picker", key);

    public static async Task FilterFilePickerAsync(this PlaywrightSdk.IPage page, string key, string filterText, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var host = page.FilePicker(key).First;
        await host.WaitForAsync(new() { State = PlaywrightSdk.WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
        var input = host.Locator("input, textarea, [role='textbox']").First;
        await input.FillAsync(filterText, new() { Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
    }

    public static async Task SelectFilePickerEntryAsync(this PlaywrightSdk.IPage page, string key, string entryName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var host = page.FilePicker(key).First;
        await host.WaitForAsync(new() { State = PlaywrightSdk.WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
        await host.GetByText(entryName, new() { Exact = false }).First.ClickAsync(ClickOpts).WaitAsync(ct).ConfigureAwait(false);
    }

    // Wave 3: IToggleSplitButton (ui.toggle-split-button)
    public static PlaywrightSdk.ILocator ToggleSplitButton(this PlaywrightSdk.IPage page, string key)
        => page.Neuron("ui.toggle-split-button", key);

    public static async Task ClickToggleSplitPrimaryAsync(this PlaywrightSdk.IPage page, string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var host = page.ToggleSplitButton(key).First;
        await host.WaitForAsync(new() { State = PlaywrightSdk.WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
        await host.Locator("[role='button'], [role='switch'], [role='checkbox']").First.ClickAsync(ClickOpts).WaitAsync(ct).ConfigureAwait(false);
    }

    public static async Task SelectToggleSplitOptionAsync(this PlaywrightSdk.IPage page, string key, string optionLabel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var host = page.ToggleSplitButton(key).First;
        await host.WaitForAsync(new() { State = PlaywrightSdk.WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
        var buttons = host.Locator("[role='button']");
        if (await buttons.CountAsync().WaitAsync(ct).ConfigureAwait(false) > 1)
        {
            await buttons.Nth(1).ClickAsync(ClickOpts).WaitAsync(ct).ConfigureAwait(false);
        }
        await page.GetByText(optionLabel, new() { Exact = false }).First.ClickAsync(ClickOpts).WaitAsync(ct).ConfigureAwait(false);
    }

    // Wave 3: IKanbanBoard (ui.kanban)
    public static PlaywrightSdk.ILocator KanbanBoard(this PlaywrightSdk.IPage page, string key)
        => page.Neuron("ui.kanban", key);

    public static async Task ClickKanbanCardAsync(this PlaywrightSdk.IPage page, string key, string cardTitle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var host = page.KanbanBoard(key).First;
        await host.WaitForAsync(new() { State = PlaywrightSdk.WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
        await host.GetByText(cardTitle, new() { Exact = false }).First.ClickAsync(ClickOpts).WaitAsync(ct).ConfigureAwait(false);
    }

    public static async Task MoveKanbanCardAsync(this PlaywrightSdk.IPage page, string key, string cardTitle, string actionOrColumnLabel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var host = page.KanbanBoard(key).First;
        await host.WaitForAsync(new() { State = PlaywrightSdk.WaitForSelectorState.Visible, Timeout = DefaultTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
        var card = host.GetByText(cardTitle, new() { Exact = false }).First;
        await card.ClickAsync(ClickOpts).WaitAsync(ct).ConfigureAwait(false);
        var target = host.GetByText(actionOrColumnLabel, new() { Exact = false }).First;
        await target.ClickAsync(ClickOpts).WaitAsync(ct).ConfigureAwait(false);
    }

    public static Task<string> ReadKanbanTextAsync(this PlaywrightSdk.IPage page, string key, CancellationToken ct = default)
        => page.ReadNeuronTextAsync("ui.kanban", key, ct);

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

    // Flame SceneView Camera HUD (zoom in, zoom out, fit, reset, zoom readout, pan/wheel gestures)
    public static PlaywrightSdk.ILocator SceneCameraHud(this PlaywrightSdk.IPage page)
        => page.KeyedNode("flame-camera-hud");

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

    public static async Task<string> ReadNeuronTextAsync(this PlaywrightSdk.IPage page, string kind, string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var host = page.Neuron(kind, key).First;
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
}
