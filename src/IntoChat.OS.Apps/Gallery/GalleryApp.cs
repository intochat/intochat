/*
# Gallery
Role: Default
Shipped: true
Operations: open, filter, select, switch-identity, ui.command

WinUI 3-style IntoChat UI Kit Gallery powered by live `IntoChat.OS.Modules.UI` neurons
(`IChart`, `IToggle`, `ICheckBox`, `ISelect`, `ITabs`, `IExpander`, `IListBox`, `ITable`).
*/
using System.Globalization;
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.UI.AutoSuggestBox;
using IntoChat.OS.Modules.UI.BreadcrumbBar;
using IntoChat.OS.Modules.UI.CalendarView;
using IntoChat.OS.Modules.UI.Chart;
using IntoChat.OS.Modules.UI.Chat;
using IntoChat.OS.Modules.UI.CheckBox;
using IntoChat.OS.Modules.UI.CodeEditor;
using IntoChat.OS.Modules.UI.ColorPicker;
using IntoChat.OS.Modules.UI.CommandBar;
using IntoChat.OS.Modules.UI.ContextMenu;
using IntoChat.OS.Modules.UI.DatePicker;
using IntoChat.OS.Modules.UI.Dialog;
using IntoChat.OS.Modules.UI.Expander;
using IntoChat.OS.Modules.UI.Form;
using IntoChat.OS.Modules.UI.GridView;
using IntoChat.OS.Modules.UI.ImageViewer;
using IntoChat.OS.Modules.UI.InfoBar;
using IntoChat.OS.Modules.UI.ListBox;
using IntoChat.OS.Modules.UI.MarkdownViewer;
using IntoChat.OS.Modules.UI.MediaPlayer;
using IntoChat.OS.Modules.UI.NumberBox;
using IntoChat.OS.Modules.UI.PersonPicture;
using IntoChat.OS.Modules.UI.Progress;
using IntoChat.OS.Modules.UI.RadioButtons;
using IntoChat.OS.Modules.UI.Rating;
using IntoChat.OS.Modules.UI.Select;
using IntoChat.OS.Modules.UI.Slider;
using IntoChat.OS.Modules.UI.SplitButton;
using IntoChat.OS.Modules.UI.Table;
using IntoChat.OS.Modules.UI.Tabs;
using IntoChat.OS.Modules.UI.TeachingTip;
using IntoChat.OS.Modules.UI.TimePicker;
using IntoChat.OS.Modules.UI.Toggle;
using IntoChat.OS.Modules.UI.TreeView;
using IntoChat.OS.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UiListBoxItem = IntoChat.OS.Modules.UI.ListBox.ListBoxItem;
using UiSelectOption = IntoChat.OS.Modules.UI.Select.SelectOption;
using UiTabItem = IntoChat.OS.Modules.UI.Tabs.TabItem;

namespace IntoChat.OS.Apps.Gallery;

[Signal("gallery.open")]
public sealed record GalleryOpen(string? Category = null) : Signal;

[Signal("gallery.filter")]
public sealed record GalleryFilter(string? Category = null, string? Search = null) : Signal;

[Signal("gallery.select")]
public sealed record GallerySelect(string? ControlId = null) : Signal;

[Signal("gallery.switch-identity")]
public sealed record GallerySwitchIdentity(string? StylePreset = null, string? BarLayout = null) : Signal;

/// <summary>
/// Live interactive Accordant-style DDD Aggregate embedded inside the Patterns & Graphs page
/// to demonstrate shared <see cref="Rule"/> guards, disabled-reason tooltips, and unfolded <see cref="StateGraph{TState}"/> topology.
/// </summary>
public sealed record BankAccount(bool IsOpen = false, string Holder = "", decimal Balance = 0m)
{
    public BankAccount() : this(false, "", 0m) { }

    [Invariant("Account balance can never drop below zero.")]
    public bool NonNegativeBalance => Balance >= 0m;

    [Invariant]
    public Rule OpenAccountHasHolder => Decision.Require(
        "holder_present_when_open",
        this,
        static a => !a.IsOpen || !string.IsNullOrWhiteSpace(a.Holder),
        "An open bank account must have a holder.");

    public Rule MustNotBeOpen => Decision.Rule("account_open", this, static a => a.IsOpen, "Account is already open.");
    public Rule MustBeOpen => Decision.Rule("not_open", this, static a => !a.IsOpen, "Open the account first.");
    public Rule PositiveAmount(decimal amount) => Decision.Rule("positive_amount", amount, static a => a <= 0m, "Amount must be positive.");

    public Rule SufficientFunds(decimal amount) => Decision.Rule(
        "sufficient_funds",
        (Account: this, Amount: amount),
        static x => !x.Account.IsOpen || x.Account.Balance < x.Amount,
        static x => string.Create(CultureInfo.InvariantCulture, $"Insufficient funds: balance ${x.Account.Balance:0}, requested ${x.Amount:0}."));

    public Decision<BankAccount> Open([Sample("alice")] string holder) =>
        Decision.Check("open", MustNotBeOpen, Decision.Rule("holder_required", holder, static h => string.IsNullOrWhiteSpace(h), "Account holder name is required."))
        ?? Decision.Accept(new BankAccount(true, holder.Trim(), 0m));

    public Decision<BankAccount> Deposit([Sample(50)] decimal amount) =>
        Decision.Check("deposit", MustBeOpen, PositiveAmount(amount))
        ?? Decision.Accept(this with { Balance = Balance + amount });

    public Decision<BankAccount> Withdraw([Sample(30, 70)] decimal amount) =>
        Decision.Check("withdraw", MustBeOpen, PositiveAmount(amount), SufficientFunds(amount))
        ?? Decision.Accept(this with { Balance = Balance - amount });

    public override string ToString() => IsOpen
        ? string.Create(CultureInfo.InvariantCulture, $"{Holder}: ${Balance:0.##}")
        : "Closed ($0)";
}

public sealed record GalleryAppState(
    string Page,
    string StylePreset,
    string BarLayout,
    string Status)
{
    public static readonly string[] PageIds =
    [
        "home",
        "colors", "typography", "icons", "spacing",
        "toggle", "checkbox", "select", "buttons", "form", "winui-inputs",
        "tabs", "expander", "listbox", "dialog", "winui-nav",
        "datetime", "media", "workspace",
        "charts", "table",
        "chat", "cards", "graphs",
    ];

    public BankAccount Account { get; init; } = new();
    public string SelectedGraphStateId { get; init; } = "s0";
    public bool ShowRejectedSelfLoops { get; init; } = true;
    public int StreamPointCounter { get; init; } = 4;
    public string RevenueQueryFilter { get; init; } = "all";
    public bool LockedToggleDisabled { get; init; } = true;
    public int ButtonClicks { get; init; }
    public string LastAction { get; init; } = "Ready · Select any page from the navigation rail.";

    public GalleryAppState() : this(
        "home",
        "warm-paper",
        "floating-glass",
        "Ready")
    {
    }

    [Invariant("Active page is always a valid Gallery page.")]
    public bool HasValidPage => PageIds.Contains(Page, StringComparer.Ordinal);

    [Invariant("Embedded BankAccount aggregate always satisfies its own non-negative balance invariant.")]
    public bool EmbeddedAccountHealthy => Account.NonNegativeBalance && !Account.OpenAccountHasHolder.Broken;

    public Rule SufficientAccountFunds(decimal amount) => Account.SufficientFunds(amount);

    public GalleryAppState SelectPage(string page)
    {
        var normalized = NormalizePage(page);
        return this with
        {
            Page = normalized,
            Status = $"Gallery · {PageTitle(normalized)}",
            LastAction = $"Navigated to {PageTitle(normalized)}",
        };
    }

    public GalleryAppState RecordButtonClick(string label) => this with
    {
        ButtonClicks = ButtonClicks + 1,
        LastAction = $"Clicked '{label}' (total clicks: {ButtonClicks + 1})",
    };

    public Decision<GalleryAppState> OpenBankAccount(string holder)
        => Account.Open(holder).Match(
            (next, _) => Decision.Accept(this with { Account = next, LastAction = $"Opened BankAccount for {next.Holder} ($0)" }),
            Decision<GalleryAppState>.Refused);

    public Decision<GalleryAppState> DepositFunds(decimal amount)
        => Account.Deposit(amount).Match(
            (next, _) => Decision.Accept(this with { Account = next, LastAction = string.Create(CultureInfo.InvariantCulture, $"Deposited ${amount:0} · Balance: ${next.Balance:0}") }),
            Decision<GalleryAppState>.Refused);

    public Decision<GalleryAppState> WithdrawFunds(decimal amount)
        => Account.Withdraw(amount).Match(
            (next, _) => Decision.Accept(this with { Account = next, LastAction = string.Create(CultureInfo.InvariantCulture, $"Withdrew ${amount:0} · Balance: ${next.Balance:0}") }),
            Decision<GalleryAppState>.Refused);

    public GalleryAppState SelectGraphState(string stateId) => this with
    {
        SelectedGraphStateId = string.IsNullOrWhiteSpace(stateId) ? "s0" : stateId.Trim(),
        LastAction = $"Inspecting unfolded state '{stateId}'",
    };

    public GalleryAppState ToggleRejectedLoops() => this with
    {
        ShowRejectedSelfLoops = !ShowRejectedSelfLoops,
        LastAction = !ShowRejectedSelfLoops ? "Showing rule-guarded self-loops" : "Hiding rule-guarded self-loops",
    };

    public static string NormalizePage(string? page) => (page ?? "home").Trim().ToLowerInvariant() switch
    {
        "colors" or "surfaces" or "design" => "colors",
        "typography" or "type" => "typography",
        "icons" or "iconography" => "icons",
        "spacing" or "layout" => "spacing",
        "toggle" or "switch" => "toggle",
        "checkbox" or "check" => "checkbox",
        "select" or "combobox" => "select",
        "buttons" or "button" or "inputs" => "buttons",
        "form" => "form",
        "winui-inputs" or "slider" or "radio" or "number" or "rating" or "color" => "winui-inputs",
        "tabs" or "tabstrip" => "tabs",
        "expander" => "expander",
        "listbox" or "list" => "listbox",
        "dialog" or "modal" => "dialog",
        "winui-nav" or "breadcrumb" or "suggest" or "tree" or "progress" or "infobar" => "winui-nav",
        "datetime" or "date" or "time" or "calendar" => "datetime",
        "media" or "grid" or "image" or "player" or "context-menu" => "media",
        "workspace" or "editor" or "markdown" or "command-bar" or "split-button" or "person" or "tip" => "workspace",
        "charts" or "chart" or "data" => "charts",
        "table" or "datatable" => "table",
        "chat" or "ino" or "devui" => "chat",
        "cards" or "status" => "cards",
        "graphs" or "graph" or "patterns" or "slots" => "graphs",
        _ => "home",
    };

    public static string PageTitle(string page) => NormalizePage(page) switch
    {
        "colors" => "Colors & Surfaces",
        "typography" => "Typography",
        "icons" => "Iconography",
        "spacing" => "Spacing & Layout",
        "toggle" => "Toggle (IToggle)",
        "checkbox" => "CheckBox (ICheckBox)",
        "select" => "Select (ISelect)",
        "buttons" => "Buttons & Actions",
        "form" => "Form & Vault Secrets (IForm)",
        "winui-inputs" => "WinUI 3 Inputs (Slider, Radio, Number, Rating, Color)",
        "tabs" => "Tabs (ITabs)",
        "expander" => "Expander (IExpander)",
        "listbox" => "ListBox (IListBox)",
        "dialog" => "Dialog (IDialog)",
        "winui-nav" => "WinUI 3 Nav & Status (Breadcrumb, Suggest, Tree, Progress, InfoBar)",
        "datetime" => "Date & Time (DatePicker, TimePicker, Calendar)",
        "media" => "Menus, Grid & Media (ContextMenu, GridView, ImageViewer, MediaPlayer)",
        "workspace" => "Workspace Kit (CommandBar, CodeEditor, Markdown, SplitButton, Person, Tip)",
        "charts" => "Charts (IChart)",
        "table" => "Data Table (ITable)",
        "chat" => "DevUI Chat (IChatThread)",
        "cards" => "Status & Chat Cards",
        "graphs" => "State Graphs & DDD Rules",
        _ => "Home",
    };
}

[Neuron("app.gallery")]
[Screen("app.gallery", Title = "UI Gallery", Icon = Icons.Sparkles)]
public sealed class GalleryApp : AggregateScreen<GalleryAppState>
{
    public const string AppName = "gallery";

    // Brain-local keys for live IntoChat.OS.Modules.UI neurons demonstrated in the Gallery.
    public const string StreamChartKey = "gallery/charts/stream";
    public const string RevenueChartKey = "gallery/charts/revenue";
    public const string WifiToggleKey = "gallery/toggles/wifi";
    public const string LockedToggleKey = "gallery/toggles/locked";
    public const string RememberCheckKey = "gallery/checkbox/remember";
    public const string ModelSelectKey = "gallery/select/model";
    public const string DemoTabsKey = "gallery/tabs/demo";
    public const string DemoExpanderKey = "gallery/expander/demo";
    public const string DemoListBoxKey = "gallery/listbox/demo";
    public const string SpansTableKey = "gallery/table/spans";
    public const string DemoFormKey = "gallery/form/demo";
    public const string DemoDialogKey = "gallery/dialog/demo";
    public const string DevUiChatKey = "gallery/chat/devui";
    public const string DemoSliderKey = "gallery/slider/demo";
    public const string DemoRadioKey = "gallery/radio/demo";
    public const string DemoNumberKey = "gallery/number/demo";
    public const string DemoRatingKey = "gallery/rating/demo";
    public const string DemoColorKey = "gallery/color/demo";
    public const string DemoBreadcrumbKey = "gallery/breadcrumb/demo";
    public const string DemoAutoSuggestKey = "gallery/auto-suggest/demo";
    public const string DemoTreeViewKey = "gallery/tree-view/demo";
    public const string DemoProgressKey = "gallery/progress/demo";
    public const string DemoInfoBarKey = "gallery/info-bar/demo";
    public const string DemoDatePickerKey = "gallery/date-picker/demo";
    public const string DemoTimePickerKey = "gallery/time-picker/demo";
    public const string DemoCalendarKey = "gallery/calendar/demo";
    public const string DemoContextMenuKey = "gallery/context-menu/demo";
    public const string DemoGridViewKey = "gallery/grid-view/demo";
    public const string DemoImageViewerKey = "gallery/image-viewer/demo";
    public const string DemoMediaPlayerKey = "gallery/media-player/demo";
    public const string DemoCommandBarKey = "gallery/command-bar/demo";
    public const string DemoCodeEditorKey = "gallery/code-editor/demo";
    public const string DemoMarkdownKey = "gallery/markdown/demo";
    public const string DemoSplitButtonKey = "gallery/split-button/demo";
    public const string DemoPersonKey = "gallery/person/demo";
    public const string DemoTeachingTipKey = "gallery/teaching-tip/demo";

    private static readonly ILogger ConsoleLogger = IntoChat.OS.Framework.Logging.OsConsoleLog.For<GalleryApp>();
    private readonly ILogger _logger;
    private bool _seeded;

    public GalleryApp() : this(null) { }

    [ActivatorUtilitiesConstructor]
    public GalleryApp(ILogger<GalleryApp>? logger = null)
    {
        _logger = (ILogger?)logger ?? ConsoleLogger;
    }

    public static AppBundle Bundle { get; } = new(
        "gallery",
        "3.0.0",
        new AppManifest(
            Title: "UI Gallery",
            Description: "WinUI 3-style IntoChat UI Kit Gallery powered by live Modules.UI neurons.",
            Operations:
            [
                new("open", "Open the WinUI 3-style UI Gallery."),
                new("filter", "Navigate to a Gallery page."),
                new("select", "Execute a Gallery interactive demo action."),
                new("switch-identity", "Switch the active StylePreset and BarLayout."),
                new(UiCommand.SignalAlias, "Dispatch a typed UI Kit aggregate command."),
            ],
            Settings: [],
            Slots: [],
            Icon: "widgets",
            Dock: 8,
            SideWidth: 256d,
            Toolbar:
            [
                new("open", Icon: "widgets", Label: "Home"),
                new("switch-identity", Icon: "palette", Label: "Identity"),
            ]),
        "",
        new Dictionary<string, string>(StringComparer.Ordinal))
    {
        CompiledType = typeof(GalleryApp).AssemblyQualifiedName,
    };

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        await base.OnActivate(cancellationToken);
        await EnsureSeededAsync();
    }

    [On]
    public async Task Open(GalleryOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureSeededAsync();

        var preset = State.StylePreset;
        var barLayout = State.BarLayout;
        if (TryGetOptional<IUserSettings>("settings") is { } settings)
        {
            var userPref = await settings.Read();
            preset = userPref.StylePreset;
            barLayout = userPref.BarLayout;
        }

        var page = string.IsNullOrWhiteSpace(request.Category) ? State.Page : GalleryAppState.NormalizePage(request.Category);
        await Show(State with
        {
            Page = page,
            StylePreset = preset,
            BarLayout = barLayout,
            Status = $"UI Gallery · {GalleryAppState.PageTitle(page)}",
        });
    }

    [On]
    public Task Filter(GalleryFilter request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var target = !string.IsNullOrWhiteSpace(request.Category) ? request.Category : request.Search;
        return Show(State.SelectPage(target ?? "home"));
    }

    [On]
    public async Task Select(GallerySelect request)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureSeededAsync();
        var raw = request.ControlId?.Trim() ?? "";

        if (raw == "stream:append")
        {
            var nextNum = State.StreamPointCounter + 1;
            var val = 55 + (nextNum * 29 % 95);
            if (TryGetOptional<IChart>(StreamChartKey) is { } chart)
            {
                await chart.Append(new ChartPoint($"T{nextNum}", val, $"evt-{nextNum}"));
            }
            await Show(State with
            {
                StreamPointCounter = nextNum,
                LastAction = $"Appended point T{nextNum} ({val}) to IChart '{StreamChartKey}'",
            });
            return;
        }

        if (raw.StartsWith("revenue:filter:", StringComparison.OrdinalIgnoreCase))
        {
            var filter = raw["revenue:filter:".Length..].Trim().ToLowerInvariant();
            if (TryGetOptional<IChart>(RevenueChartKey) is { } chart)
            {
                var sourceAddress = new NeuronId(Id.Brain, "ui.sample-chart-source", "default").ToString();
                await chart.Bind(sourceAddress, new ChartQuery(Filter: filter));
            }
            await Show(State with
            {
                RevenueQueryFilter = filter,
                LastAction = $"Bound IChart '{RevenueChartKey}' to SampleChartSource (filter: {filter})",
            });
            return;
        }

        if (raw == "toggle:lock")
        {
            var nextDisabled = !State.LockedToggleDisabled;
            if (TryGetOptional<IToggle>(LockedToggleKey) is { } toggle)
            {
                await toggle.SetEnabled(!nextDisabled);
            }
            await Show(State with
            {
                LockedToggleDisabled = nextDisabled,
                LastAction = nextDisabled ? "Locked secondary IToggle neuron" : "Unlocked secondary IToggle neuron",
            });
            return;
        }

        if (raw == "dialog:open")
        {
            if (TryGetOptional<IDialog>(DemoDialogKey) is { } dlg)
            {
                await dlg.Open("Create folder", "Enter a folder name to test IDialog confirm-with-input.", "Create", "Cancel", inputLabel: "Folder name", inputPlaceholder: "e.g. q4-reports");
            }
            await Show(State with { LastAction = "Opened live IDialog neuron" });
            return;
        }

        if (raw.StartsWith("page:", StringComparison.OrdinalIgnoreCase))
        {
            await Show(State.SelectPage(raw["page:".Length..]));
            return;
        }

        await Show(State.SelectPage(raw));
    }

    [On]
    public async Task SwitchIdentity(GallerySwitchIdentity request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var preset = string.IsNullOrWhiteSpace(request.StylePreset) ? State.StylePreset : request.StylePreset.Trim();
        var barLayout = string.IsNullOrWhiteSpace(request.BarLayout) ? GalleryLibrary.DefaultBarLayoutFor(preset, State.BarLayout) : request.BarLayout.Trim();
        if (TryGetOptional<IUserSettings>("settings") is { } settings)
        {
            var saved = await settings.SetUiIdentity(preset, barLayout);
            preset = saved.StylePreset;
            barLayout = saved.BarLayout;
        }

        await Show(State with
        {
            StylePreset = preset,
            BarLayout = barLayout,
            Status = $"Active UI identity: {preset}",
            LastAction = $"Switched OS identity to '{preset}' ({barLayout})",
        });
    }

    private async Task EnsureSeededAsync()
    {
        if (_seeded) { return; }
        _seeded = true;
        try
        {
            if (TryGetOptional<IChart>(StreamChartKey) is { } streamChart && (await streamChart.Read()).Version == 0)
            {
                await streamChart.Render(new ChartSpec(
                    Title: "Live Signal Stream (Append)",
                    Kind: ChartKinds.Line,
                    Series:
                    [
                        new ChartSeries("Signals/s",
                        [
                            new ChartPoint("T1", 42, "evt-1"),
                            new ChartPoint("T2", 68, "evt-2"),
                            new ChartPoint("T3", 59, "evt-3"),
                            new ChartPoint("T4", 84, "evt-4"),
                        ]),
                    ],
                    Description: "Independent streaming IChart neuron (200-point rolling window, EventId dedup). Click 'Append point' or switch chart kind.",
                    From: "Stream"));
            }

            if (TryGetOptional<IChart>(RevenueChartKey) is { } revenueChart && (await revenueChart.Read()).Version == 0)
            {
                var sourceAddress = new NeuronId(Id.Brain, "ui.sample-chart-source", "default").ToString();
                await revenueChart.Bind(sourceAddress, new ChartQuery(Filter: "all"));
            }

            if (TryGetOptional<IToggle>(WifiToggleKey) is { } wifi && (await wifi.Read()).Version == 0)
            {
                await wifi.Configure("Live telemetry stream", isOn: true, isEnabled: true, onLabel: "Active", offLabel: "Paused");
            }

            if (TryGetOptional<IToggle>(LockedToggleKey) is { } locked && (await locked.Read()).Version == 0)
            {
                await locked.Configure("Protected policy switch", isOn: true, isEnabled: false, onLabel: "Enforced", offLabel: "Off");
            }

            if (TryGetOptional<ICheckBox>(RememberCheckKey) is { } check && (await check.Read()).Version == 0)
            {
                await check.Configure("Persist workspace layout across restarts", isChecked: true);
            }

            if (TryGetOptional<ISelect>(ModelSelectKey) is { } select && (await select.Read()).Version == 0)
            {
                await select.Configure("Default reasoning model",
                [
                    new UiSelectOption("claude-sonnet-4-5", "Claude Sonnet 4.5", "Balanced coding & reasoning"),
                    new UiSelectOption("gpt-5-thinking", "GPT-5 Thinking", "Deep multi-step planning"),
                    new UiSelectOption("qwen3.5:4b", "Qwen 3.5 4B (Local)", "Zero-cost local Ollama"),
                ], "claude-sonnet-4-5");
            }

            if (TryGetOptional<ITabs>(DemoTabsKey) is { } tabs && (await tabs.Read()).Version == 0)
            {
                await tabs.Configure(
                [
                    new UiTabItem("overview", "Overview"),
                    new UiTabItem("signals", "Signals"),
                    new UiTabItem("contracts", "Contracts"),
                ], "overview");
            }

            if (TryGetOptional<IExpander>(DemoExpanderKey) is { } expander && (await expander.Read()).Version == 0)
            {
                await expander.Configure(
                    "Why every control is a Neuron",
                    "Each interactive control in IntoChat.OS.Modules.UI owns durable state, enforces invariants, emits typed signals, and can be addressed identically by Flutter, Ino, MCP, and C# synapses.",
                    isExpanded: true);
            }

            if (TryGetOptional<IListBox>(DemoListBoxKey) is { } listBox && (await listBox.Read()).Version == 0)
            {
                await listBox.Configure(
                [
                    new UiListBoxItem("chart", "IChart", "Streaming + IChartSource query binding", Group: "Data", Icon: "chart-column"),
                    new UiListBoxItem("table", "ITable", "Filter, sort, paginate via ITableSource", Group: "Data", Icon: "table"),
                    new UiListBoxItem("toggle", "IToggle", "Durable boolean switch neuron", Group: "Input", Icon: "sliders"),
                    new UiListBoxItem("select", "ISelect", "Typed option picker neuron", Group: "Input", Icon: "list"),
                ], "chart");
            }

            if (TryGetOptional<ITable>(SpansTableKey) is { } table && (await table.Read()).Version == 0)
            {
                var tableSource = new NeuronId(Id.Brain, "ui.sample-table-source", "default").ToString();
                await table.Bind(
                    tableSource,
                    "Live Silo Telemetry Spans",
                    [
                        new TableColumn("span", "Span"),
                        new TableColumn("status", "Status"),
                        new TableColumn("latency", "Duration"),
                        new TableColumn("module", "Module"),
                    ],
                    pageSize: 5);
            }

            if (TryGetOptional<IForm>(DemoFormKey) is { } form && (await form.Read()).Version == 0)
            {
                await form.Configure("Workspace & Vault Secret Form",
                [
                    new FormField("displayName", "Display name", FormFieldKinds.Text, "Alex", Required: true),
                    new FormField("apiKey", "Provider API key (Vault SecretRef)", FormFieldKinds.Secret, "Paste token and press Enter to seal"),
                ],
                new Dictionary<string, string>(StringComparer.Ordinal) { ["displayName"] = "Alex" },
                submitLabel: "Save form");
            }

            if (TryGetOptional<IChatThread>(DevUiChatKey) is { } chat && (await chat.Read()).Version == 0)
            {
                await chat.Configure("demo", "Ino DevUI Workbench", "claude-sonnet-4-5",
                [
                    new ChatModelOption("claude-sonnet-4-5", "Claude Sonnet 4.5", "Anthropic"),
                    new ChatModelOption("gpt-5-thinking", "GPT-5 Thinking", "OpenAI"),
                    new ChatModelOption("qwen3.5:4b", "Qwen 3.5 4B (Local)", "Ollama"),
                ], voiceAvailable: true);
                await chat.AppendMessage(new ChatMessage("u1", "user", "Compare Q1 vs Q2 throughput and explain the spike."));
                await chat.AppendMessage(new ChatMessage(
                    Id: "a1",
                    Role: "assistant",
                    Text: "Throughput grew +66% from Jan (68) to Jun (156). Below is the live query-bound `IChart` neuron — you can switch its kind or click any bar directly inside this chat turn.",
                    Reasoning: "1. Query SampleChartSource via IChart.Bind.\n2. Compare 2026 Active series vs 2025 Baseline.\n3. Embed live ui.chart neuron inline in the reply.",
                    ToolCalls:
                    [
                        new ChatToolCall("tc-1", "ui.chart.bind", "{\"source\":\"ui.sample-chart-source\",\"filter\":\"all\"}", "{\"series\":2,\"points\":12,\"status\":\"ready\"}", "done", 14),
                    ],
                    Slots: [new ChatInlineSlot("ui.chart", RevenueChartKey)],
                    Status: "done",
                    InputTokens: 1240,
                    OutputTokens: 215));
                await chat.SetContext(
                [
                    new ChatContextItem("user.profile", "Name: Owner · Theme: Warm Paper", "owner", 85),
                    new ChatContextItem("memory.recall", "Prefers WinUI 3 layout + DDD UI neurons", "owner", 140),
                    new ChatContextItem("apps.capabilities", "12 Modules.UI contracts available", "platform", 190),
                ], "Ready · Click Compact to summarize older turns.", 1240, 215);
            }

            if (TryGetOptional<ISlider>(DemoSliderKey) is { } slider && (await slider.Read()).Version == 0)
            {
                await slider.Configure("Reasoning budget", min: 0, max: 100, step: 5, value: 65, unit: "%");
            }
            if (TryGetOptional<IRadioButtons>(DemoRadioKey) is { } radio && (await radio.Read()).Version == 0)
            {
                await radio.Configure("Execution topology",
                [
                    new RadioOption("local", "Local Orleans Silo"),
                    new RadioOption("cloud", "Azure Cloud Control Plane"),
                    new RadioOption("hybrid", "Hybrid Local + Cloud"),
                ], "local");
            }
            if (TryGetOptional<INumberBox>(DemoNumberKey) is { } number && (await number.Read()).Version == 0)
            {
                await number.Configure("Monthly spend cap", value: 50, min: 5, max: 500, step: 10, prefix: "$");
            }
            if (TryGetOptional<IRating>(DemoRatingKey) is { } rating && (await rating.Read()).Version == 0)
            {
                await rating.Configure("Response quality", value: 4, max: 5);
            }
            if (TryGetOptional<IColorPicker>(DemoColorKey) is { } color && (await color.Read()).Version == 0)
            {
                await color.Configure("Accent color", "#1F5EFF");
            }
            if (TryGetOptional<IBreadcrumbBar>(DemoBreadcrumbKey) is { } crumb && (await crumb.Read()).Version == 0)
            {
                await crumb.SetItems([new BreadcrumbItem("root", "Workspace"), new BreadcrumbItem("docs", "Documents"), new BreadcrumbItem("q4", "Q4 Reports")]);
            }
            if (TryGetOptional<IAutoSuggestBox>(DemoAutoSuggestKey) is { } suggest && (await suggest.Read()).Version == 0)
            {
                await suggest.Configure("Search 21 Modules.UI neurons…",
                    ["IChart", "ITable", "IChatThread", "IToggle", "ICheckBox", "ISelect", "ITabs", "IExpander", "IListBox", "IForm", "IDialog", "ISlider", "IRadioButtons", "INumberBox", "IRating", "IColorPicker", "IBreadcrumbBar", "IAutoSuggestBox", "ITreeView", "IProgressIndicator", "IInfoBar"]);
            }
            if (TryGetOptional<ITreeView>(DemoTreeViewKey) is { } tree && (await tree.Read()).Version == 0)
            {
                await tree.Configure(
                [
                    new TreeNode("src", "src", IsFolder: true),
                    new TreeNode("ui", "Modules/UI", ParentId: "src", IsFolder: true),
                    new TreeNode("chart", "ChartNeuron.cs", ParentId: "ui"),
                    new TreeNode("chat", "ChatThreadNeuron.cs", ParentId: "ui"),
                    new TreeNode("apps", "IntoChat.OS.Apps", ParentId: "src", IsFolder: true),
                    new TreeNode("gallery", "GalleryApp.cs", ParentId: "apps"),
                ], expandedIds: ["src", "ui"], selectedId: "chart");
            }
            if (TryGetOptional<IProgressIndicator>(DemoProgressKey) is { } prog && (await prog.Read()).Version == 0)
            {
                await prog.SetProgress("Indexing brain knowledge graph", 0.78, isIndeterminate: false, kind: "bar");
            }
            if (TryGetOptional<IInfoBar>(DemoInfoBarKey) is { } info && (await info.Read()).Version == 0)
            {
                await info.Show("WinUI 3 Neuron Kit", "All 28 interactive controls are real DigitalBrain neurons synced over gRPC.", severity: "success");
            }
            if (TryGetOptional<IDatePicker>(DemoDatePickerKey) is { } dp && (await dp.Read()).Version == 0)
            {
                await dp.Configure("Release target date", "2026-11-19", minDate: "2026-01-01", maxDate: "2027-12-31");
            }
            if (TryGetOptional<ITimePicker>(DemoTimePickerKey) is { } tp && (await tp.Read()).Version == 0)
            {
                await tp.Configure("Daily digest time", 9, 30, use24Hour: true);
            }
            if (TryGetOptional<ICalendarView>(DemoCalendarKey) is { } cal && (await cal.Read()).Version == 0)
            {
                await cal.Configure("Sprint milestones", 2026, 11, ["2026-11-19", "2026-11-24"], multiSelect: true);
            }
            if (TryGetOptional<IContextMenu>(DemoContextMenuKey) is { } menu && (await menu.Read()).Version == 0)
            {
                await menu.Configure("File Actions",
                [
                    new ContextMenuItem("open", "Open in Editor", "file-code", "Ctrl+O"),
                    new ContextMenuItem("rename", "Rename", "edit", "F2"),
                    new ContextMenuItem("delete", "Delete", "trash", "Del", Destructive: true),
                ]);
            }
            if (TryGetOptional<IGridView>(DemoGridViewKey) is { } gv && (await gv.Read()).Version == 0)
            {
                await gv.Configure("Workspace Assets",
                [
                    new GridViewItem("arch", "architecture.md", "14 KB · Markdown", "file-text", Badge: "DOC"),
                    new GridViewItem("hero", "hero-banner.png", "1.2 MB · PNG", "image", Badge: "IMG"),
                    new GridViewItem("demo", "walkthrough.mp4", "24 MB · Video", "film", Badge: "VID"),
                    new GridViewItem("data", "q4-metrics.csv", "82 KB · Table", "table", Badge: "CSV"),
                ], selectedId: "arch", columns: 2);
            }
            if (TryGetOptional<IImageViewer>(DemoImageViewerKey) is { } img && (await img.Read()).Version == 0)
            {
                await img.Configure("Design System Preview", "", "Interactive zoom & rotation backed by IImageViewer neuron");
            }
            if (TryGetOptional<IMediaPlayer>(DemoMediaPlayerKey) is { } mp && (await mp.Read()).Version == 0)
            {
                await mp.Configure("IntoChat OS Ambient Focus", "Neural Audio Engine", "", kind: "audio", durationSeconds: 215);
            }
            if (TryGetOptional<ICommandBar>(DemoCommandBarKey) is { } cb && (await cb.Read()).Version == 0)
            {
                await cb.Configure(
                [
                    new CommandBarItem("run", "Run", "play"),
                    new CommandBarItem("format", "Format", "sparkles"),
                    new CommandBarItem("share", "Share", "share-2"),
                    new CommandBarItem("export", "Export JSON", "download", Primary: false),
                    new CommandBarItem("reset", "Reset State", "refresh-cw", Primary: false),
                ]);
            }
            if (TryGetOptional<ICodeEditor>(DemoCodeEditorKey) is { } ed && (await ed.Read()).Version == 0)
            {
                await ed.Configure("ChartDemo.cs", "csharp",
                    "var chart = Get<IChart>(\"gallery/charts/stream\");\nawait chart.Append(new ChartPoint(\"T6\", 94, \"evt-6\"));");
            }
            if (TryGetOptional<IMarkdownViewer>(DemoMarkdownKey) is { } md && (await md.Read()).Version == 0)
            {
                await md.SetDocument("Architecture Note", "## IntoChat UI Neurons\nEvery interactive widget is a **durable DDD neuron** under `IntoChat.OS.Modules.UI` synced 1-to-1 over gRPC.");
            }
            if (TryGetOptional<ISplitButton>(DemoSplitButtonKey) is { } sb && (await sb.Read()).Version == 0)
            {
                await sb.Configure("Build & Test",
                [
                    new SplitButtonOption("build-test", "Build & Test", "play"),
                    new SplitButtonOption("build-only", "Build Only", "hammer"),
                    new SplitButtonOption("e2e", "Run Chromium E2E", "globe"),
                ], "play", "build-test");
            }
            if (TryGetOptional<IPersonPicture>(DemoPersonKey) is { } pp && (await pp.Read()).Version == 0)
            {
                await pp.Configure("Ino Assistant", "Claude Sonnet 4.5 · Ready", presence: "online");
            }
            if (TryGetOptional<ITeachingTip>(DemoTeachingTipKey) is { } tip && (await tip.Read()).Version == 0)
            {
                await tip.Show("Direct gRPC Neurons", "All 34 UI controls talk directly to DigitalBrain neurons with zero host round-trips.", "Explore Charts");
            }
        }
#pragma warning disable CA1031 // Seeding is best-effort when running in unit tests without UI module registered.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogDebug(ex, "Gallery UI neuron seeding skipped in minimal test host.");
        }
    }

    public new GalleryAppState State => base.State;

    public View Render() => PreviewView(State);

    protected override Node Render(GalleryAppState state)
        => GalleryRendering.RenderRoot(state, LastRejection);

    private T? TryGetOptional<T>(string key) where T : class
    {
        try { return Get<T>(key); }
#pragma warning disable CA1031 // Optional service in unit tests.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogDebug(ex, "{Service} is optional when GalleryApp runs in tests.", typeof(T).Name);
            return null;
        }
    }
}
