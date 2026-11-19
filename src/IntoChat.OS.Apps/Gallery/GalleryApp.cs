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
using IntoChat.OS.Modules.UI.Chart;
using IntoChat.OS.Modules.UI.Chat;
using IntoChat.OS.Modules.UI.CheckBox;
using IntoChat.OS.Modules.UI.Dialog;
using IntoChat.OS.Modules.UI.Expander;
using IntoChat.OS.Modules.UI.Form;
using IntoChat.OS.Modules.UI.ListBox;
using IntoChat.OS.Modules.UI.Select;
using IntoChat.OS.Modules.UI.Table;
using IntoChat.OS.Modules.UI.Tabs;
using IntoChat.OS.Modules.UI.Toggle;
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
        "toggle", "checkbox", "select", "buttons", "form",
        "tabs", "expander", "listbox", "dialog",
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
        "tabs" or "tabstrip" => "tabs",
        "expander" => "expander",
        "listbox" or "list" => "listbox",
        "dialog" or "modal" => "dialog",
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
        "tabs" => "Tabs (ITabs)",
        "expander" => "Expander (IExpander)",
        "listbox" => "ListBox (IListBox)",
        "dialog" => "Dialog (IDialog)",
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
