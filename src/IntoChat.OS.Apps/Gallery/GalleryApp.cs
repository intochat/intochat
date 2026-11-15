/*
# Gallery
Role: Default
Shipped: true
Operations: open, filter, select, switch-identity, ui.command

IntoChat Kit Gallery & DDD Aggregate Reactor Showcase (`UK-9` / `INT-1357`).
Showcases all 7 canonical UI Kit component families across interactive tabs, an embedded live
`BankAccount` DDD Aggregate with `Rule` guards (`SufficientFunds`) and hover tooltips, `accent` +
`chartContext` charts, sortable tables, live `Slot` composition, and an Accordant-style unfolded
`StateGraphView` generated directly via `AggregateExplorer<BankAccount>`.
*/
using System.Globalization;
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.UI;
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
/// Live interactive Accordant-style DDD Aggregate embedded inside the Kit Gallery (`UK-9`)
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
        !IsOpen || !string.IsNullOrWhiteSpace(Holder),
        "An open bank account must have a holder.");

    public Rule MustNotBeOpen => Decision.Rule("account_open", IsOpen, "Account is already open.");
    public Rule MustBeOpen => Decision.Rule("not_open", !IsOpen, "Open the account first.");
    public Rule PositiveAmount(decimal amount) => Decision.Rule("positive_amount", amount <= 0m, "Amount must be positive.");

    public Rule SufficientFunds(decimal amount) => Decision.Rule(
        "sufficient_funds",
        !IsOpen || Balance < amount,
        string.Create(CultureInfo.InvariantCulture, $"Insufficient funds: balance ${Balance:0}, requested ${amount:0}."));

    public Decision<BankAccount> Open([Sample("alice")] string holder) =>
        Decision.Check("open", MustNotBeOpen, Decision.Rule("holder_required", string.IsNullOrWhiteSpace(holder), "Account holder name is required."))
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
    string Category,
    string Search,
    string SelectedId,
    string StylePreset,
    string BarLayout,
    string Status,
    GalleryCatalogEntry[] Entries)
{
    public static readonly string[] ShowcaseTabIds = ["foundation", "inputs", "data", "slots", "catalog"];

    public string ShowcaseTab { get; init; } = "foundation";
    public BankAccount Account { get; init; } = new();
    public string TableSortColumn { get; init; } = "latency";
    public bool TableSortDescending { get; init; }
    public string SelectedGraphStateId { get; init; } = "s0";
    public bool ShowRejectedSelfLoops { get; init; } = true;
    public string ViewMode { get; init; } = "detail";
    public string KnobStyle { get; init; } = "primary";
    public string KnobTone { get; init; } = "accent";
    public bool KnobSmall { get; init; }
    public string KnobState { get; init; } = "interactive";
    public string KnobSurface { get; init; } = "elevated";
    public string KnobChartKind { get; init; } = "bar";
    public string CodeTab { get; init; } = "csharp";
    public int SignalPulseCount { get; init; }
    public bool SwitchValue { get; init; } = true;
    public bool SwitchLocked { get; init; }
    public string SelectedOption { get; init; } = "claude-sonnet-4-5";
    public string ShellNav { get; init; } = "overview";
    public string ApprovalStatus { get; init; } = "pending";
    public int? ChoicePickedIndex { get; init; }
    public string DraftStatus { get; init; } = "pending";
    public string SecretStatus { get; init; } = "pending";
    public string JobStatus { get; init; } = "running";
    public string SelectedBrainNode { get; init; } = "os";

    public GalleryAppState() : this(
        "all",
        "",
        "approval-card",
        "fluent-acrylic",
        "floating-glass",
        "Ready",
        GalleryLibrary.DefaultCatalog)
    {
    }

    [Invariant("Showcase tab is always one of the canonical Kit Gallery tabs.")]
    public bool HasValidShowcaseTab => ShowcaseTabIds.Contains(ShowcaseTab, StringComparer.Ordinal);

    [Invariant("Embedded BankAccount aggregate always satisfies its own non-negative balance invariant.")]
    public bool EmbeddedAccountHealthy => Account.NonNegativeBalance && !Account.OpenAccountHasHolder.Broken;

    public Rule SufficientAccountFunds(decimal amount) => Account.SufficientFunds(amount);

    public GalleryAppState SelectTab([Sample("foundation", "inputs", "data", "slots", "catalog")] string tab)
    {
        var normalized = NormalizeShowcaseTab(tab);
        return this with
        {
            ShowcaseTab = normalized,
            Status = $"Kit Gallery · {ShowcaseTabTitle(normalized)}",
        };
    }

    public GalleryAppState SetSearch([Sample("", "Table")] string query)
    {
        var trimmed = query?.Trim() ?? string.Empty;
        var matches = GalleryLibrary.FilteredEntries(Entries, Category, trimmed);
        var selected = matches.Any(e => e.Id == SelectedId)
            ? SelectedId
            : matches.FirstOrDefault()?.Id ?? SelectedId;
        return this with
        {
            Search = trimmed,
            SelectedId = selected,
            Status = $"Filtered {matches.Length} component(s) in {GalleryLibrary.CategoryDisplayName(Category)}",
        };
    }

    public GalleryAppState SelectCategory([Sample("all", "chat-lfp")] string category)
    {
        var normalized = GalleryLibrary.NormalizeCategory(category);
        var matches = GalleryLibrary.FilteredEntries(Entries, normalized, Search);
        var selected = matches.Any(e => e.Id == SelectedId)
            ? SelectedId
            : matches.FirstOrDefault()?.Id ?? SelectedId;
        return this with
        {
            Category = normalized,
            SelectedId = selected,
            ShowcaseTab = "catalog",
            Status = $"Category · {GalleryLibrary.CategoryDisplayName(normalized)} ({matches.Length})",
        };
    }

    [IgnoreExplore]
    public GalleryAppState InspectControl(string controlId)
    {
        var raw = string.IsNullOrWhiteSpace(controlId) ? "approval-card" : controlId.Trim();
        var normalizedId = raw.Equals("brain-3d", StringComparison.OrdinalIgnoreCase)
            ? "brain-scene-3d"
            : raw.Equals("acrylic", StringComparison.OrdinalIgnoreCase)
                ? "acrylic-surface"
                : raw;
        var match = Entries.FirstOrDefault(e => string.Equals(e.Id, normalizedId, StringComparison.OrdinalIgnoreCase)) ?? Entries[0];
        return this with
        {
            SelectedId = match.Id,
            ViewMode = "detail",
            ShowcaseTab = "catalog",
            Status = $"Inspecting {match.Title} ({match.NeuronContract})",
        };
    }

    [IgnoreExplore]
    public GalleryAppState SetCatalogViewMode(string mode) => this with
    {
        ViewMode = string.Equals(mode, "grid", StringComparison.OrdinalIgnoreCase) ? "grid" : "detail",
        ShowcaseTab = "catalog",
        Status = string.Equals(mode, "grid", StringComparison.OrdinalIgnoreCase)
            ? $"Catalog grid · {GalleryLibrary.FilteredEntries(Entries, Category, Search).Length} components"
            : $"Inspecting {SelectedId}",
    };

    public Decision<GalleryAppState> OpenBankAccount([Sample("alice")] string holder)
    {
        var outcome = Account.Open(holder);
        if (outcome.IsRefused)
        {
            return Decision.Refuse<GalleryAppState>(outcome.Rejection);
        }

        return Decision.Accept(
            this with
            {
                ShowcaseTab = "inputs",
                Account = outcome.Next,
                Status = $"Opened account for {outcome.Next.Holder} ($0)",
            },
            new GallerySelect("bank:open"));
    }

    [IgnoreExplore]
    public GalleryAppState ResetBankAccount() => this with
    {
        ShowcaseTab = "inputs",
        Account = new BankAccount(),
        Status = "Reset BankAccount aggregate to closed ($0)",
    };

    public Decision<GalleryAppState> DepositFunds([Sample(50)] decimal amount)
    {
        var outcome = Account.Deposit(amount);
        if (outcome.IsRefused)
        {
            return Decision.Refuse<GalleryAppState>(outcome.Rejection);
        }

        return Decision.Accept(
            this with
            {
                ShowcaseTab = "inputs",
                Account = outcome.Next,
                Status = string.Create(CultureInfo.InvariantCulture, $"Deposited +${amount:0} · Balance ${outcome.Next.Balance:0}"),
            },
            new GallerySelect("bank:deposit"));
    }

    public Decision<GalleryAppState> WithdrawFunds([Sample(30, 70)] decimal amount)
    {
        var outcome = Account.Withdraw(amount);
        if (outcome.IsRefused)
        {
            return Decision.Refuse<GalleryAppState>(outcome.Rejection);
        }

        return Decision.Accept(
            this with
            {
                ShowcaseTab = "inputs",
                Account = outcome.Next,
                Status = string.Create(CultureInfo.InvariantCulture, $"Withdrew -${amount:0} · Balance ${outcome.Next.Balance:0}"),
            },
            new GallerySelect("bank:withdraw"));
    }

    public GalleryAppState SortTableBy([Sample("latency", "span")] string column)
    {
        var normalized = column?.Trim().ToLowerInvariant() switch
        {
            "span" => "span",
            "module" => "module",
            "status" => "status",
            _ => "latency",
        };
        var desc = string.Equals(TableSortColumn, normalized, StringComparison.Ordinal) && !TableSortDescending;
        return this with
        {
            ShowcaseTab = "data",
            TableSortColumn = normalized,
            TableSortDescending = desc,
            Status = $"Sorted telemetry spans by {normalized} ({(desc ? "descending" : "ascending")})",
        };
    }

    [IgnoreExplore]
    public GalleryAppState SetChartKind(string chartKind) => this with
    {
        ShowcaseTab = "data",
        KnobChartKind = string.Equals(chartKind, "line", StringComparison.OrdinalIgnoreCase) ? "line" : "bar",
        Status = $"Chart mode · {(string.Equals(chartKind, "line", StringComparison.OrdinalIgnoreCase) ? "Line chart" : "Column chart")}",
    };

    [IgnoreExplore]
    public GalleryAppState ToggleLiveStream() => this with
    {
        SwitchValue = !SwitchValue,
        Status = !SwitchValue ? "Live signal stream active" : "Live signal stream paused",
    };

    [IgnoreExplore]
    public GalleryAppState ToggleSwitchLock() => this with
    {
        SwitchLocked = !SwitchLocked,
        Status = SwitchLocked ? "Unlocked switch policy" : "Locked switch policy",
    };

    [IgnoreExplore]
    public GalleryAppState SelectModelOption(string model) => this with
    {
        SelectedOption = string.IsNullOrWhiteSpace(model) ? SelectedOption : model.Trim(),
        Status = $"Selected model · {model}",
    };

    [IgnoreExplore]
    public GalleryAppState SelectGraphState(string stateId) => this with
    {
        ShowcaseTab = "slots",
        SelectedGraphStateId = string.IsNullOrWhiteSpace(stateId) ? "s0" : stateId.Trim(),
        Status = $"Inspecting unfolded state {stateId}",
    };

    [IgnoreExplore]
    public GalleryAppState ToggleRejectedLoops() => this with
    {
        ShowcaseTab = "slots",
        ShowRejectedSelfLoops = !ShowRejectedSelfLoops,
        Status = !ShowRejectedSelfLoops ? "Showing guarded self-loops" : "Hiding guarded self-loops",
    };

    [IgnoreExplore]
    public GalleryAppState PickChoice(int index) => this with
    {
        ChoicePickedIndex = index,
        SignalPulseCount = SignalPulseCount + 1,
        Status = $"Choice prompt selected option #{index + 1}",
    };

    [IgnoreExplore]
    public GalleryAppState SetSecretVaultStatus(string status) => this with
    {
        SecretStatus = status,
        SignalPulseCount = SignalPulseCount + 1,
        Status = $"Credential prompt · {status}",
    };

    public static string NormalizeShowcaseTab(string? tab) => (tab ?? "foundation").Trim().ToLowerInvariant() switch
    {
        "inputs" or "guards" or "controls" => "inputs",
        "data" or "charts" => "data",
        "slots" or "graph" or "state-graph" => "slots",
        "catalog" or "inspector" => "catalog",
        _ => "foundation",
    };

    public static string ShowcaseTabTitle(string tab) => NormalizeShowcaseTab(tab) switch
    {
        "inputs" => "Inputs & rule guards",
        "data" => "Data & charts",
        "slots" => "Live slots & Accordant state graph",
        "catalog" => "Component catalog",
        _ => "Foundation & status",
    };

    public override string ToString() => $"{ShowcaseTab}:{Account}|sort={TableSortColumn}{(TableSortDescending ? "-" : "+")}";
}

[Neuron("app.gallery")]
[Screen("app.gallery", Title = "UI Gallery", Icon = Icons.Sparkles)]
public sealed class GalleryApp : AggregateScreen<GalleryAppState>
{
    public const string AppName = "gallery";

    public static AppBundle Bundle { get; } = new(
        "gallery",
        "2.0.0",
        new AppManifest(
            Title: "UI Gallery",
            Description: "Interactive IntoChat UI Kit showcase, live BankAccount DDD rule guards, and Accordant state graph explorer.",
            Operations:
            [
                new("open", "Open the UI Kit gallery catalog."),
                new("filter", "Filter controls by category or search query."),
                new("select", "Inspect a control's NodeContract, [Signal]s, and neuron:// URI."),
                new("switch-identity", "Switch the active StylePreset and BarLayout."),
                new(UiCommand.SignalAlias, "Dispatch a typed UI Kit aggregate command."),
            ],
            Settings: [],
            Slots: [],
            Icon: "widgets",
            Dock: 8,
            SideWidth: 268d,
            Toolbar:
            [
                new("open", Icon: "widgets", Label: "Catalog"),
                new("switch-identity", Icon: "palette", Label: "Switch identity"),
            ]),
        "",
        new Dictionary<string, string>(StringComparer.Ordinal))
    {
        CompiledType = typeof(GalleryApp).AssemblyQualifiedName,
    };

    [On]
    public async Task Open(GalleryOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var preset = State.StylePreset;
        var barLayout = State.BarLayout;
        if (TryGetOptional<IUserSettings>("settings") is { } settings)
        {
            var userPref = await settings.Read();
            preset = userPref.StylePreset;
            barLayout = userPref.BarLayout;
        }

        var category = string.IsNullOrWhiteSpace(request.Category) ? State.Category : GalleryLibrary.NormalizeCategory(request.Category);
        var matches = GalleryLibrary.FilteredEntries(State.Entries, category, State.Search);
        var selected = matches.Any(e => e.Id == State.SelectedId)
            ? State.SelectedId
            : matches.FirstOrDefault()?.Id ?? State.SelectedId;

        await Show(State with
        {
            Category = category,
            SelectedId = selected,
            StylePreset = preset,
            BarLayout = barLayout,
            Status = $"UI Gallery ready · {State.Entries.Length} controls loaded · Category: {category}",
        });
    }

    [On]
    public Task Filter(GalleryFilter request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var rawCategory = request.Category?.Trim() ?? string.Empty;
        var viewMode = State.ViewMode;
        if (rawCategory.StartsWith("detail:", StringComparison.OrdinalIgnoreCase))
        {
            rawCategory = rawCategory["detail:".Length..];
            viewMode = "detail";
        }
        else if (rawCategory.StartsWith("grid:", StringComparison.OrdinalIgnoreCase))
        {
            rawCategory = rawCategory["grid:".Length..];
            viewMode = "grid";
        }

        var category = string.IsNullOrWhiteSpace(rawCategory) ? State.Category : GalleryLibrary.NormalizeCategory(rawCategory);
        var search = request.Search is null ? State.Search : request.Search.Trim();
        var matches = GalleryLibrary.FilteredEntries(State.Entries, category, search);
        var selected = matches.Any(e => e.Id == State.SelectedId)
            ? State.SelectedId
            : matches.FirstOrDefault()?.Id ?? State.SelectedId;
        return Show(State with
        {
            Category = category,
            Search = search,
            SelectedId = selected,
            ViewMode = viewMode,
            Status = $"Filtered {matches.Length} control(s) in '{category}'",
        });
    }

    [On]
    public Task Select(GallerySelect request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var raw = string.IsNullOrWhiteSpace(request.ControlId) ? "approval-card" : request.ControlId.Trim();

        if (raw.StartsWith("showcase:", StringComparison.OrdinalIgnoreCase))
        {
            return Show(State.SelectTab(raw["showcase:".Length..]));
        }

        if (string.Equals(raw, "home", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "grid", StringComparison.OrdinalIgnoreCase))
        {
            return Show(State with
            {
                ShowcaseTab = "catalog",
                ViewMode = "grid",
                Status = $"Catalog grid · {GalleryLibrary.FilteredEntries(State.Entries, State.Category, State.Search).Length} controls",
            });
        }

        if (string.Equals(raw, "detail", StringComparison.OrdinalIgnoreCase))
        {
            return Show(State with { ShowcaseTab = "catalog", ViewMode = "detail", Status = $"Inspecting {State.SelectedId}" });
        }

        if (string.Equals(raw, "pulse", StringComparison.OrdinalIgnoreCase))
        {
            var nextPulse = State.SignalPulseCount + 1;
            var currentEntry = State.Entries.FirstOrDefault(e => e.Id == State.SelectedId) ?? State.Entries[0];
            return Show(State with
            {
                SignalPulseCount = nextPulse,
                Status = $"Fired [Signal] {currentEntry.Signals.FirstOrDefault() ?? "gallery.select"} (#{nextPulse})",
            });
        }

        if (raw.StartsWith("tab:", StringComparison.OrdinalIgnoreCase))
        {
            var tab = raw["tab:".Length..].Trim().ToLowerInvariant();
            return Show(State with { CodeTab = tab is "flutter" or "contract" ? tab : "csharp", Status = $"Code inspector tab: {tab}" });
        }

        if (raw.StartsWith("knob:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = raw.Split(':', 3, StringSplitOptions.TrimEntries);
            var knobKind = parts.ElementAtOrDefault(1)?.ToLowerInvariant() ?? "";
            var knobVal = parts.ElementAtOrDefault(2)?.ToLowerInvariant() ?? "";
            return Show(knobKind switch
            {
                "style" => State with { KnobStyle = knobVal, Status = $"Property knob · Style = {knobVal}" },
                "tone" => State with { KnobTone = knobVal, Status = $"Property knob · Tone = {knobVal}" },
                "size" => State with { KnobSmall = knobVal is "small" or "true", Status = $"Property knob · Size = {(knobVal is "small" or "true" ? "Small (30px)" : "Standard (36px)")}" },
                "state" => State with { KnobState = knobVal, Status = $"Property knob · State = {knobVal}" },
                "surface" => State with { KnobSurface = knobVal, Status = $"Property knob · Surface = {knobVal}" },
                "chart" => State with { KnobChartKind = knobVal, Status = $"Property knob · ChartKind = {knobVal}" },
                _ => State,
            });
        }

        if (raw.StartsWith("toggle:", StringComparison.OrdinalIgnoreCase))
        {
            var target = raw["toggle:".Length..].Trim().ToLowerInvariant();
            return Show(target switch
            {
                "lock" => State.ToggleSwitchLock(),
                _ => State.ToggleLiveStream(),
            });
        }

        if (raw.StartsWith("option:", StringComparison.OrdinalIgnoreCase))
        {
            return Show(State.SelectModelOption(raw["option:".Length..]));
        }

        if (raw.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
        {
            var nav = raw["shell:".Length..].Trim().ToLowerInvariant();
            return Show(State with { ShellNav = nav, Status = $"Shell navigation tab: {nav}" });
        }

        if (raw.StartsWith("card:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = raw.Split(':', 3, StringSplitOptions.TrimEntries);
            var cardKind = parts.ElementAtOrDefault(1)?.ToLowerInvariant() ?? "";
            var cardAct = parts.ElementAtOrDefault(2)?.ToLowerInvariant() ?? "";
            return Show(cardKind switch
            {
                "approval" => State with { ApprovalStatus = cardAct, SignalPulseCount = State.SignalPulseCount + 1, Status = $"ApprovalCard: {cardAct}" },
                "choice" => State with { ChoicePickedIndex = int.TryParse(cardAct, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx) ? idx : 0, SignalPulseCount = State.SignalPulseCount + 1, Status = $"ChoiceCard picked option #{cardAct}" },
                "draft" => State with { DraftStatus = cardAct, SignalPulseCount = State.SignalPulseCount + 1, Status = $"DraftCard: {cardAct}" },
                "secret" => State with { SecretStatus = cardAct, SignalPulseCount = State.SignalPulseCount + 1, Status = $"SecretCard: {cardAct}" },
                "job" => State with { JobStatus = cardAct, SignalPulseCount = State.SignalPulseCount + 1, Status = $"JobCard: {cardAct}" },
                _ => State,
            });
        }

        if (raw.StartsWith("node:", StringComparison.OrdinalIgnoreCase))
        {
            var nodeId = raw["node:".Length..].Trim();
            return Show(State with { SelectedBrainNode = nodeId, Status = $"Inspecting brain node: {nodeId}" });
        }

        return Show(State.InspectControl(raw));
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
            Status = $"Active UI identity: {preset} ({barLayout})",
        });
    }

    public new GalleryAppState State => base.State;

    public View Render() => PreviewView(State);

    protected override Node Render(GalleryAppState state)
        => GalleryRendering.RenderRoot(state, LastRejection);

    private static readonly ILogger<GalleryApp> Log = LoggerFactory.Create(_ => { }).CreateLogger<GalleryApp>();

    private T? TryGetOptional<T>(string key) where T : class
    {
        try { return Get<T>(key); }
        catch (Exception ex)
        {
            Log.LogDebug(ex, "{Service} is optional when GalleryApp runs in tests.", typeof(T).Name);
            return null;
        }
    }
}
