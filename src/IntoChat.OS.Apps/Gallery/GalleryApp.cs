/*
# Gallery
Role: Default
Shipped: true
Operations: open, filter, select, switch-identity, ui.command

WinUI 3-style IntoChat UI Kit Gallery powered by live `IntoChat.OS.Modules.UI` neurons
(`IChart`, `ITabs`, `ITable`, `IForm`, `IChatThread`).
*/
using IntoChat.OS.Host.Apps;
using System.Globalization;
using DigitalBrain;
using DigitalBrain.Components;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.UI.BreadcrumbBar;
using IntoChat.OS.Modules.UI.Chart;
using IntoChat.OS.Modules.UI.Chat;
using IntoChat.OS.Modules.UI.CodeEditor;
using IntoChat.OS.Modules.UI.CommandBar;
using IntoChat.OS.Modules.UI.ContextMenu;
using IntoChat.OS.Modules.UI.Dialog;
using IntoChat.OS.Modules.UI.Form;
using IntoChat.OS.Modules.UI.GridView;
using IntoChat.OS.Modules.UI.KanbanBoard;
using IntoChat.OS.Modules.UI.MarkdownViewer;
using IntoChat.OS.Modules.UI.PropertyGrid;
using IntoChat.OS.Modules.UI.Slider;
using IntoChat.OS.Modules.UI.Stepper;
using IntoChat.OS.Modules.UI.Table;
using IntoChat.OS.Modules.UI.Tabs;
using IntoChat.OS.Modules.UI.TreeView;
using IntoChat.OS.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
        "buttons", "form", "winui-inputs",
        "tabs", "dialog", "winui-nav",
        "media", "workspace", "wave3",
        "charts", "table",
        "chat", "cards", "graphs",
    ];

    public BankAccount Account { get; init; } = new();
    public string SelectedId { get; init; } = "approval-card";
    public string SelectedGraphStateId { get; init; } = "s0";
    public bool ShowRejectedSelfLoops { get; init; } = true;
    public int StreamPointCounter { get; init; } = 4;
    public string RevenueQueryFilter { get; init; } = "all";
    public int ButtonClicks { get; init; }
    public string IconCategory { get; init; } = "All";
    public string CodeLanguage { get; init; } = "csharp";
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
        "buttons" or "button" or "inputs" => "buttons",
        "form" => "form",
        "winui-inputs" or "slider" => "winui-inputs",
        "tabs" or "tabstrip" => "tabs",
        "dialog" or "modal" => "dialog",
        "winui-nav" or "breadcrumb" or "tree" => "winui-nav",
        "media" or "grid" or "image" or "context-menu" => "media",
        "workspace" or "editor" or "markdown" or "command-bar" => "workspace",
        "wave3" or "stepper" or "property-grid" or "kanban" => "wave3",
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
        "buttons" => "Buttons & Actions",
        "form" => "Form & Vault Secrets (IForm)",
        "winui-inputs" => "WinUI 3 Inputs (Slider)",
        "tabs" => "Tabs (ITabs)",
        "dialog" => "Dialog (IDialog)",
        "winui-nav" => "WinUI 3 Nav (Breadcrumb, Tree)",
        "media" => "Menus, Grid & Media (ContextMenu, GridView, Image)",
        "workspace" => "Workspace Kit (CommandBar, CodeEditor, Markdown)",
        "wave3" => "WinUI 3 Wave 3 (Stepper, PropertyGrid, Kanban)",
        "charts" => "Charts (IChart)",
        "table" => "Data Table (ITable)",
        "chat" => "DevUI Chat (IChatThread)",
        "cards" => "Status & Chat Cards",
        "graphs" => "State Graphs & DDD Rules",
        _ => "Home",
    };
}

[Neuron("app.gallery")]
[Screen("app.gallery", Title = "UI Gallery", Icon = "layout-grid", Category = "Studio", Dock = 8)]
public sealed partial class GalleryApp : AggregateScreen<GalleryAppState>
{
    public const string AppName = "gallery";

    // Brain-local keys for live IntoChat.OS.Modules.UI neurons demonstrated in the Gallery.
    public const string StreamChartKey = "gallery/charts/stream";
    public const string RevenueChartKey = "gallery/charts/revenue";
    public const string DemoTabsKey = "gallery/tabs/demo";
    public const string SpansTableKey = "gallery/table/spans";
    public const string DemoFormKey = "gallery/form/demo";
    public const string DemoDialogKey = "gallery/dialog/demo";
    public const string DevUiChatKey = "gallery/chat/devui";
    public const string DemoSliderKey = "gallery/slider/demo";
    public const string DemoBreadcrumbKey = "gallery/breadcrumb/demo";
    public const string DemoTreeViewKey = "gallery/tree-view/demo";
    public const string DemoContextMenuKey = "gallery/context-menu/demo";
    public const string DemoGridViewKey = "gallery/grid-view/demo";
    public const string DemoCommandBarKey = "gallery/command-bar/demo";
    public const string DemoCodeEditorKey = "gallery/code-editor/demo";
    public const string DemoMarkdownKey = "gallery/markdown/demo";
    public const string DemoStepperKey = "gallery/stepper/demo";
    public const string DemoPropertyGridKey = "gallery/property-grid/demo";
    public const string DemoKanbanKey = "gallery/kanban/demo";
    public const string IconGridKey = "gallery/icons/grid";

    [Slot(Key = "settings", Required = false)] private partial IUserSettings? UserSettings { get; }
    [Slot(Key = StreamChartKey, Required = false)] private partial IChart? StreamChart { get; }
    [Slot(Key = RevenueChartKey, Required = false)] private partial IChart? RevenueChart { get; }
    [Slot(Key = DemoTabsKey, Required = false)] private partial ITabs? DemoTabs { get; }
    [Slot(Key = SpansTableKey, Required = false)] private partial ITable? SpansTable { get; }
    [Slot(Key = DemoFormKey, Required = false)] private partial IForm? DemoForm { get; }
    [Slot(Key = DemoDialogKey, Required = false)] private partial IDialog? DemoDialog { get; }
    [Slot(Key = DevUiChatKey, Required = false)] private partial IChatThread? DevUiChat { get; }
    [Slot(Key = DemoSliderKey, Required = false)] private partial ISlider? DemoSlider { get; }
    [Slot(Key = DemoBreadcrumbKey, Required = false)] private partial IBreadcrumbBar? DemoBreadcrumb { get; }
    [Slot(Key = DemoTreeViewKey, Required = false)] private partial ITreeView? DemoTreeView { get; }
    [Slot(Key = DemoContextMenuKey, Required = false)] private partial IContextMenu? DemoContextMenu { get; }
    [Slot(Key = DemoGridViewKey, Required = false)] private partial IGridView? DemoGridView { get; }
    [Slot(Key = DemoCommandBarKey, Required = false)] private partial ICommandBar? DemoCommandBar { get; }
    [Slot(Key = DemoCodeEditorKey, Required = false)] private partial ICodeEditor? DemoCodeEditor { get; }
    [Slot(Key = DemoMarkdownKey, Required = false)] private partial IMarkdownViewer? DemoMarkdown { get; }
    [Slot(Key = DemoStepperKey, Required = false)] private partial IStepper? DemoStepper { get; }
    [Slot(Key = DemoPropertyGridKey, Required = false)] private partial IPropertyGrid? DemoPropertyGrid { get; }
    [Slot(Key = DemoKanbanKey, Required = false)] private partial IKanbanBoard? DemoKanban { get; }
    [Slot(Key = IconGridKey, Required = false)] private partial IGridView? IconGrid { get; }

    private static readonly ILogger ConsoleLogger = IntoChat.OS.Host.Logging.OsConsoleLog.For<GalleryApp>();
    private readonly ILogger _logger;
    private bool _seeded;

    public GalleryApp() : this(null) { }

    [ActivatorUtilitiesConstructor]
    public GalleryApp(ILogger<GalleryApp>? logger = null)
    {
        _logger = (ILogger?)logger ?? ConsoleLogger;
    }

    public static AppBundle Bundle => AppBundle.FromScreen<GalleryApp>(
        "gallery",
        "3.0.0",
        description: "WinUI 3-style IntoChat UI Kit Gallery powered by live Modules.UI neurons.",
        operations:
        [
            new("open", "Open the WinUI 3-style UI Gallery."),
            new("filter", "Navigate to a Gallery page."),
            new("select", "Execute a Gallery interactive demo action."),
            new("switch-identity", "Switch the active StylePreset and BarLayout."),
            new(UiCommand.SignalAlias, "Dispatch a typed UI Kit aggregate command."),
        ],
        sideWidth: 268d,
        toolbar:
        [
            new("open", Icon: "widgets", Label: "Home"),
            new("switch-identity", Icon: "palette", Label: "Identity"),
        ]);

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        AppActivationScope.Notify(this);
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
        if (UserSettings is { } settings)
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
        var normalizedCategory = GalleryLibrary.NormalizeCategory(request.Category ?? "all");
        var matches = GalleryLibrary.FilteredEntries(GalleryLibrary.DefaultCatalog, normalizedCategory, request.Search ?? "");
        var nextSelectedId = matches.Any(e => e.Id == State.SelectedId)
            ? State.SelectedId
            : matches.FirstOrDefault()?.Id ?? State.SelectedId;

        var target = !string.IsNullOrWhiteSpace(request.Category) ? request.Category : request.Search;
        return Show(State.SelectPage(target ?? "home") with { SelectedId = nextSelectedId });
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
            if (StreamChart is { } chart)
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
            if (RevenueChart is { } chart)
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

        if (raw == "dialog:open")
        {
            if (DemoDialog is { } dlg)
            {
                await dlg.Open("Create folder", "Enter a folder name to test IDialog confirm-with-input.", "Create", "Cancel", inputLabel: "Folder name", inputPlaceholder: "e.g. q4-reports");
            }
            await Show(State with { LastAction = "Opened live IDialog neuron" });
            return;
        }

        if (raw.StartsWith("icons:cat:", StringComparison.OrdinalIgnoreCase))
        {
            var cat = raw["icons:cat:".Length..].Trim();
            var resolved = GalleryIconCatalog.Categories.FirstOrDefault(c => string.Equals(c, cat, StringComparison.OrdinalIgnoreCase)) ?? "All";
            await SeedIconBrowserAsync(resolved);
            await Show(State with
            {
                IconCategory = resolved,
                LastAction = $"Filtered Iconography catalog to '{resolved}'",
            });
            return;
        }

        if (raw.StartsWith("code:lang:", StringComparison.OrdinalIgnoreCase))
        {
            var lang = raw["code:lang:".Length..].Trim().ToLowerInvariant();
            if (DemoCodeEditor is { } ed)
            {
                var (fileTitle, sample) = CodeSampleForLanguage(lang);
                await ed.Configure(fileTitle, lang, sample);
            }
            await Show(State with
            {
                CodeLanguage = lang,
                LastAction = $"Switched ICodeEditor syntax highlighting to '{lang}'",
            });
            return;
        }

        if (raw.StartsWith("page:", StringComparison.OrdinalIgnoreCase))
        {
            await Show(State.SelectPage(raw["page:".Length..]));
            return;
        }

        if (GalleryLibrary.DefaultCatalog.FirstOrDefault(e => string.Equals(e.Id, raw, StringComparison.OrdinalIgnoreCase)) is { } catalogEntry)
        {
            await Show(State.SelectPage(raw) with { SelectedId = catalogEntry.Id });
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
        if (UserSettings is { } settings)
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
            if (StreamChart is { } streamChart && (await streamChart.Read()).Version == 0)
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

            if (RevenueChart is { } revenueChart && (await revenueChart.Read()).Version == 0)
            {
                var sourceAddress = new NeuronId(Id.Brain, "ui.sample-chart-source", "default").ToString();
                await revenueChart.Bind(sourceAddress, new ChartQuery(Filter: "all"));
            }

            if (DemoTabs is { } tabs && (await tabs.Read()).Version == 0)
            {
                await tabs.Configure(
                [
                    new UiTabItem("overview", "Overview"),
                    new UiTabItem("signals", "Signals"),
                    new UiTabItem("contracts", "Contracts"),
                ], "overview");
            }

            if (SpansTable is { } table && (await table.Read()).Version == 0)
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

            if (DemoForm is { } form && (await form.Read()).Version == 0)
            {
                await form.Configure("Workspace & Vault Secret Form",
                [
                    new FormField("displayName", "Display name", FormFieldKinds.Text, "Alex", Required: true),
                    new FormField("apiKey", "Provider API key (Vault SecretRef)", FormFieldKinds.Secret, "Paste token and press Enter to seal"),
                ],
                new Dictionary<string, string>(StringComparer.Ordinal) { ["displayName"] = "Alex" },
                submitLabel: "Save form");
            }

            if (DevUiChat is { } chat && (await chat.Read()).Version == 0)
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
                    new ChatContextItem("apps.capabilities", "18 Modules.UI contracts available", "platform", 190),
                ], "Ready · Click Compact to summarize older turns.", 1240, 215);
            }

            if (DemoSlider is { } slider && (await slider.Read()).Version == 0)
            {
                await slider.Configure("Reasoning budget", min: 0, max: 100, step: 5, value: 65, unit: "%");
            }
            if (DemoBreadcrumb is { } crumb && (await crumb.Read()).Version == 0)
            {
                await crumb.SetItems([new BreadcrumbItem("root", "Workspace"), new BreadcrumbItem("docs", "Documents"), new BreadcrumbItem("q4", "Q4 Reports")]);
            }
            if (DemoTreeView is { } tree && (await tree.Read()).Version == 0)
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
            if (DemoContextMenu is { } menu && (await menu.Read()).Version == 0)
            {
                await menu.Configure("File Actions",
                [
                    new ContextMenuItem("open", "Open in Editor", "file-code", "Ctrl+O"),
                    new ContextMenuItem("rename", "Rename", "edit", "F2"),
                    new ContextMenuItem("delete", "Delete", "trash", "Del", Destructive: true),
                ]);
            }
            if (DemoGridView is { } gv && (await gv.Read()).Version == 0)
            {
                await gv.Configure("Workspace Assets",
                [
                    new GridViewItem("arch", "architecture.md", "14 KB · Markdown", "file-text", Badge: "DOC"),
                    new GridViewItem("hero", "hero-banner.png", "1.2 MB · PNG", "image", Badge: "IMG"),
                    new GridViewItem("demo", "walkthrough.mp4", "24 MB · Video", "film", Badge: "VID"),
                    new GridViewItem("data", "q4-metrics.csv", "82 KB · Table", "table", Badge: "CSV"),
                ], selectedId: "arch", columns: 2);
            }
            if (DemoCommandBar is { } cb && (await cb.Read()).Version == 0)
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
            if (DemoCodeEditor is { } ed && (await ed.Read()).Version == 0)
            {
                await ed.Configure("ChartDemo.cs", "csharp",
                    "var chart = Get<IChart>(\"gallery/charts/stream\");\nawait chart.Append(new ChartPoint(\"T6\", 94, \"evt-6\"));");
            }
            if (DemoMarkdown is { } md && (await md.Read()).Version == 0)
            {
                await md.SetDocument("Architecture Note", "## IntoChat UI Neurons\nEvery interactive widget is a **durable DDD neuron** under `IntoChat.OS.Modules.UI` synced 1-to-1 over gRPC.");
            }
            if (DemoStepper is { } stepper && (await stepper.Read()).Version == 0)
            {
                await stepper.Configure(
                [
                    new StepperStep("plan", "Brainstorm & Plan", StepperStatuses.Done, "Architect + Critic"),
                    new StepperStep("worktree", "Prepare Worktree", StepperStatuses.Done, "Isolated lease"),
                    new StepperStep("implement", "Implement Tasks", StepperStatuses.Active, "Roslyn + Dart"),
                    new StepperStep("verify", "Live Verify", StepperStatuses.Pending, "Playwright E2E"),
                    new StepperStep("pr", "Review & PR", StepperStatuses.Pending, "Approval gate"),
                ], activeStepId: "implement", title: "Engineer Autonomous Pipeline");
            }
            if (DemoPropertyGrid is { } propGrid && (await propGrid.Read()).Version == 0)
            {
                await propGrid.LoadJson(
                    "Live Neuron State",
                    "{\"neuron\":\"app.gallery\",\"version\":3,\"healthy\":true,\"metrics\":{\"uiNeurons\":18,\"p95Ms\":4.2},\"tags\":[\"winui3\",\"grpc\"]}",
                    readOnly: false);
            }
            if (DemoKanban is { } kanban && (await kanban.Read()).Version == 0)
            {
                await kanban.Configure(
                    "Sprint Delivery Board",
                    [
                        new KanbanColumn("backlog", "Backlog", "list-todo"),
                        new KanbanColumn("in-progress", "In Progress", "zap", WipLimit: 4),
                        new KanbanColumn("done", "Done", "circle-check"),
                    ],
                    [
                        new KanbanCard("c1", "backlog", "FilePicker VFS binding", "Canonical neuron:// URIs", "UI", "ui-kit", 0),
                        new KanbanCard("c2", "in-progress", "Engineer Worktree Flow", "Parallel sandbox leases", "Flow", "engineer", 0),
                        new KanbanCard("c3", "done", "Stepper & PropertyGrid", "Milestone 1 contracts", "DDD", "ui-kit", 0),
                    ],
                    selectedCardId: "c1");
            }
            if (IconGrid is { } iconGrid && (await iconGrid.Read()).Version == 0)
            {
                await SeedIconBrowserAsync("All");
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

    private async Task SeedIconBrowserAsync(string category)
    {
        var entries = GalleryIconCatalog.ForCategory(category);
        if (IconGrid is { } grid)
        {
            await grid.Configure(
                $"Icon Catalog · {category} ({entries.Length} glyphs · click any tile to copy slug)",
                entries.Select(e => new GridViewItem(e.Slug, e.Slug, e.Category, e.Slug, Badge: e.Badge)).ToArray(),
                selectedId: entries.FirstOrDefault()?.Slug,
                columns: 4);
        }
    }

    private static (string Title, string Sample) CodeSampleForLanguage(string lang) => lang switch
    {
        "dart" => ("CodeEditorDemo.dart", "final theme = buildIcCodeHighlightTheme(IcTheme.of(context), 'dart');\nreturn CodeEditor(controller: controller, style: CodeEditorStyle(codeTheme: theme));"),
        "json" => ("manifest.json", "{\n  \"module\": \"ui\",\n  \"neurons\": 18,\n  \"syntaxHighlight\": true\n}"),
        "sql" => ("telemetry.sql", "SELECT module, COUNT(*) AS spans, AVG(duration_ms) AS avg_ms\nFROM neuron_telemetry\nWHERE status = 'done'\nGROUP BY module;"),
        "typescript" => ("client.ts", "export interface NeuronRef {\n  readonly kind: string;\n  readonly key: string;\n}\nconst stepper: NeuronRef = { kind: 'ui.stepper', key: 'engineer/pipeline' };"),
        "yaml" => ("pipeline.yaml", "pipeline: engineer\nstages:\n  - id: brainstorm\n    status: done\n  - id: verify\n    browser: chromium"),
        "markdown" => ("ARCHITECTURE.md", "# IntoChat UI Neurons\n\n- **18 stateful controls** synced over `gRPC`\n- Live `re_highlight` syntax coloring in light & dark presets"),
        _ => ("ChartDemo.cs", "var chart = Get<IChart>(\"gallery/charts/stream\");\nawait chart.Append(new ChartPoint(\"T6\", 94, \"evt-6\"));"),
    };
}
