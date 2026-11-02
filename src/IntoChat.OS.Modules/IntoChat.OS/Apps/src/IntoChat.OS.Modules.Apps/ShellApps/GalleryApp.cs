/*
# Gallery
Role: Default
Shipped: true
Operations: open, filter, select, switch-identity

WinUI 3 Gallery-inspired interactive UI Kit and Neuron contract catalog for IntoChat OS.
Displays live component previews, NodeContract schemas, [Signal] round-trips, neuron:// URIs,
interactive Property Knobs, dynamic C# / Flutter / Contract code inspection, and allows switching
the active UI identity preset and bar layout.
*/
using System.Globalization;
using DigitalBrain.UI;

namespace IntoChat.OS.Modules.Apps;

[DigitalBrain.Signal("gallery.open")]
public sealed record GalleryOpen(string? Category = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("gallery.filter")]
public sealed record GalleryFilter(string? Category = null, string? Search = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("gallery.select")]
public sealed record GallerySelect(string? ControlId = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("gallery.switch-identity")]
public sealed record GallerySwitchIdentity(string? StylePreset = null, string? BarLayout = null) : DigitalBrain.Signal;

public sealed record GalleryCatalogEntry(
    string Id,
    string Category,
    string Title,
    string Summary,
    string NeuronContract,
    string[] Signals,
    string SourceNeuronUri,
    string CSharpSample)
{
    public string Icon { get; init; } = "sparkles";
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
    public string ViewMode { get; init; } = "detail";
    public string KnobStyle { get; init; } = "primary";
    public string KnobTone { get; init; } = "accent";
    public bool KnobSmall { get; init; }
    public string KnobState { get; init; } = "interactive";
    public string KnobSurface { get; init; } = "acrylic";
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
        DefaultCatalog)
    {
    }

    public static readonly GalleryCatalogEntry[] DefaultCatalog =
    [
        // 1. Design & Identity ("design")
        new(
            "themes",
            "design",
            "7 UI Identities & Glass Materiality",
            "Fluent Acrylic, Spatial Glass, Cyber Synaptic, Warm Paper, Warm Mica, Linear Carbon & Nordic Tactical.",
            "IntoChat.OS.Identity.IUserSettings@1",
            ["gallery.switch-identity", "settings.save-preferences"],
            "neuron://local/ui.gallery/themes",
            "await settings.SetUiIdentity(\"fluent-acrylic\", \"floating-glass\");")
        { Icon = "palette" },
        new(
            "typography",
            "design",
            "Typography (Inter + InterDisplay + tnum)",
            "Inter and InterDisplay scale with strict tabular numerals (tnum) for live telemetry.",
            "IntoChat.OS.Modules.Flutter.Text.IText@1",
            ["TextChanged"],
            "neuron://local/ui.gallery/typography",
            "new Heading(\"InterDisplay 32 · 1,492,048 tok\", Level: 1, Subtitle: \"Tabular numerals keep live telemetry aligned.\");")
        { Icon = "type" },
        new(
            "iconography",
            "design",
            "Iconography & Squircle IcAppIcon Builder",
            "Two-stop gradient squircle app badges with 1px specular border and custom/Lucide vector glyphs.",
            "DigitalBrain.AppDefinition@1",
            ["AppRegisteredSignal"],
            "neuron://local/ui.gallery/iconography",
            "[DigitalBrain.App(\"gallery\", Accent = \"#22C55E\", Category = \"Studio\")]")
        { Icon = "sparkles" },
        new(
            "acrylic-surface",
            "design",
            "AcrylicSurface & Specular Border",
            "Translucent dark obsidian glass with 24px backdrop blur and 1px top-lit specular border.",
            "IntoChat.UI.Surface@1",
            ["settings.save-preferences"],
            "neuron://local/ui.gallery/acrylic-surface",
            "Ui.AppShell(\"shell\", sidebar: Ui.NavItem(\"nav\", \"Overview\", \"layout-grid\", \"open\", selected: true), body: Ui.StatCard(\"kpi\", \"Blur\", \"28px\", \"+100%\", BadgeTone.Accent));")
        { Icon = "layers" },

        // 2. Basic Input ("controls")
        new(
            "buttons",
            "controls",
            "Btn (Styles, Sizes & Icons)",
            "Primary, Secondary, Ghost, and Danger buttons in Standard (36px) and Small (30px) sizes.",
            "IntoChat.OS.Modules.Flutter.Button.IButton@1",
            ["ButtonClickedSignal"],
            "neuron://local/ui.gallery/buttons",
            "new Button(\"Execute Action\", new ButtonAction(\"gallery\", \"select\", [\"pulse\"]), Tone: ButtonTone.Accent) { Icon = \"sparkles\", Small = false };")
        { Icon = "mouse-pointer-click" },
        new(
            "inputs",
            "controls",
            "Field & TextField",
            "Desktop-first EditableText fields with neuron:// address validation and instant submit.",
            "IntoChat.OS.Modules.Flutter.TextField.ITextField@1",
            ["TextFieldChanged"],
            "neuron://local/ui.gallery/inputs",
            "new TextField(\"address\", \"neuron://nordlys/apps.ino/main\", \"Enter neuron:// address…\") { OnSubmit = new ButtonAction(\"gallery\", \"select\", [\"pulse\"]) };")
        { Icon = "text-cursor-input" },
        new(
            "switch",
            "controls",
            "IcSwitch (with Lock State)",
            "Instant-save toggle switch with policy lock indicator and keyboard focus ring.",
            "IntoChat.OS.Modules.Flutter.Switch.ISwitch@1",
            ["SwitchToggledSignal"],
            "neuron://local/ui.gallery/switch",
            "new Switch(\"Live [Signal] stream active\", IsOn: true, Action: new ButtonAction(\"gallery\", \"select\", [\"toggle:switch\"]), Locked: false);")
        { Icon = "toggle-right" },
        new(
            "select",
            "controls",
            "Select & Dropdown",
            "Single-choice segmented and dropdown selectors backed by DigitalBrain.UI.Select.",
            "IntoChat.OS.Modules.Flutter.Select.ISelect@1",
            ["SelectionChangedSignal"],
            "neuron://local/ui.gallery/select",
            "new Select(\"Active Reasoning Model\", \"claude-sonnet-4-5\", [new SelectOption(\"claude-sonnet-4-5\", \"Claude Sonnet 4.5\"), new SelectOption(\"gpt-5-thinking\", \"GPT-5 Thinking\")]);")
        { Icon = "list-filter" },
        new(
            "tag-pill",
            "controls",
            "TagPill & Status Badges",
            "Semantic status pills (Neutral, Accent, Done, Working, Error) paired with vector glyphs.",
            "IntoChat.OS.Modules.Flutter.Badge.IBadge@1",
            ["StatusToneChanged"],
            "neuron://local/ui.gallery/tag-pill",
            "new Row([new TagPill(\"NEUTRAL\", \"neutral\", \"sparkles\"), new TagPill(\"ACCENT\", \"accent\", \"zap\"), new TagPill(\"DONE\", \"done\", \"check\")]);")
        { Icon = "tag" },
        new(
            "progress-infobar",
            "controls",
            "Progress & InfoBar",
            "Determinate progress bars and contextual status banners with action buttons.",
            "IntoChat.OS.Modules.Flutter.InfoBar.IInfoBar@1",
            ["InfoBarUpdatedSignal"],
            "neuron://local/ui.gallery/progress-infobar",
            "new InfoBar(\"Orleans grain pool synced\", \"14/14 [Scenario] gates passed without warnings.\", Tone: \"info\");")
        { Icon = "loader" },
        new(
            "files-neuron-uri",
            "controls",
            "Distributed neuron:// File Addressing",
            "Hybrid cloud + local file explorer exposing canonical neuron://{brain}/{connection}.files/{path} URIs.",
            "IntoChat.UI.Collection@1",
            ["files.location", "files.selected"],
            "neuron://owner/drive.files/projects/context.md",
            "var uri = filesApp.NeuronUriOf(selectedItem);")
        { Icon = "folder" },

        // 3. Layout & App Shell ("layout")
        new(
            "app-shell",
            "layout",
            "Ui.AppShell & Ui.StatCard Builders",
            "Ergonomic 30-line C# desktop layout builder with sidebar, toolbar, body, and split inspector.",
            "DigitalBrain.UI.AppShellNode@1",
            ["gallery.select"],
            "neuron://local/ui.gallery/app-shell",
            "Ui.AppShell(\"studio\", sidebar: Ui.NavItem(\"n1\", \"Studio\", \"sparkles\", \"open\", selected: true), body: Ui.StatCard(\"s1\", \"Throughput\", \"142 sig/m\", \"+18%\", BadgeTone.Success));")
        { Icon = "panel-left" },
        new(
            "tabs",
            "layout",
            "Tabs & Segmented Views",
            "Multi-pane tab strips switching live DigitalBrain.UI view subtrees.",
            "IntoChat.OS.Modules.Flutter.Tabs.ITabs@1",
            ["ActiveTabChanged"],
            "neuron://local/ui.gallery/tabs",
            "new Tabs(\"overview\", [new TabItem(\"overview\", \"Overview\", new ButtonAction(\"gallery\", \"select\", [\"shell:overview\"])), new TabItem(\"signals\", \"Signals\", new ButtonAction(\"gallery\", \"select\", [\"shell:signals\"]))]);")
        { Icon = "folder-kanban" },
        new(
            "surfaces",
            "layout",
            "IcAcrylicBox Surfaces",
            "Acrylic, Elevated, and Recessed containers with adaptive border hairlines.",
            "IntoChat.OS.Modules.Flutter.Surface.ISurface@1",
            ["SurfaceMutatedSignal"],
            "neuron://local/ui.gallery/surfaces",
            "new Surface([new Text(\"Adaptive glass surface\")], Kind: SurfaceKind.Acrylic, Title: \"Acrylic Surface\", Subtitle: \"1px specular border\");")
        { Icon = "square-stack" },
        new(
            "stat-card",
            "layout",
            "StatCard (Ui.StatCard)",
            "Tabular KPI metric cards with semantic delta TagPill and vector icon.",
            "DigitalBrain.UI.StatCardNode@1",
            ["MetricRefreshedSignal"],
            "neuron://local/ui.gallery/stat-card",
            "Ui.StatCard(\"kpi-tokens\", \"Compute Throughput\", \"1,248,920 tok\", \"+14.2%\", BadgeTone.Done, \"zap\");")
        { Icon = "trending-up" },

        // 4. Data & 13 LFP Chat Cards ("chat-lfp")
        new(
            "table-card",
            "chat-lfp",
            "TableCard & Postgres SQL Result (LFP)",
            "Interactive sortable data table rendered from IPostgresServer.Query or live neuron state.",
            "IntoChat.UI.TableCard@1",
            ["ino.connect-db", "ino.query-db"],
            "neuron://local/postgres/main#query-result",
            "await ConnectDb(new InoConnectDb(\"Host=localhost;Database=nordlys;Username=postgres\"));\nawait QueryDb(new InoQueryDb(\"SELECT account, stage, amount FROM opportunities\"));")
        { Icon = "table" },
        new(
            "chart-card",
            "chat-lfp",
            "ChartCard (LFP)",
            "Inline column and line chart card with comparison series and metric summary.",
            "IntoChat.UI.ChartCard@1",
            ["ino.send"],
            "neuron://local/ui.gallery/chart-card",
            "new Entry(\"chart-q4\", \"Won per month, 2026\", \"neuron://local/analytics/q4#chart\");")
        { Icon = "chart-column" },
        new(
            "approval-card",
            "chat-lfp",
            "ApprovalCard (LFP)",
            "Inline human-in-the-loop confirmation card rendered inside Ino chat turns and Smart Desktop.",
            "IntoChat.UI.ApprovalCard@1",
            ["ino.approve-card"],
            "neuron://local/os.confirmations/inbox#approval-card",
            "await Send(new InoSend(\"Draft confirmation card\"));\nawait ApproveCard(new InoApproveCard(\"card-1\", Approved: true));")
        { Icon = "check-check" },
        new(
            "choice-card",
            "chat-lfp",
            "ChoiceCard (LFP)",
            "Numbered disambiguation options rendered inline in Ino sessions.",
            "IntoChat.UI.ChoiceCard@1",
            ["ChoiceSelectedSignal"],
            "neuron://local/apps/ino#choice-card",
            "new ChoiceCardNode(\"choice-1\", \"Which Nordlys workspace should receive the Q4 renewal brief?\", [new ChoiceOptionNode(\"Nordlys Enterprise\", \"neuron://nordlys/circles/enterprise\")]);")
        { Icon = "list-checks" },
        new(
            "draft-card",
            "chat-lfp",
            "DraftCard (LFP)",
            "Inline editable email/Slack/SMS draft with Send, Schedule, and Discard actions.",
            "IntoChat.UI.DraftCard@1",
            ["DraftDispatchedSignal"],
            "neuron://local/apps/ino#draft-card",
            "new DraftCardNode(\"draft-1\", \"email\", \"jeff@intochat.os\", \"Kari Holm <kari@nordlys.no>\", \"Confirmed: Q4 renewal close date updated to 15 Nov 2026.\", Subject: \"Nordlys Q4 Enterprise Renewal\");")
        { Icon = "mail" },
        new(
            "change-preview-card",
            "chat-lfp",
            "ChangePreviewCard (LFP)",
            "Field-by-field CRM mutation preview with inline before/after values and confirmation.",
            "IntoChat.UI.ChangePreviewCard@1",
            ["ChangeAppliedSignal"],
            "neuron://local/salesforce.crm/opportunities#change-preview",
            "new ApprovalCardNode(\"change-1\", \"Update Opportunity · Nordlys Q4 Enterprise\", \"Salesforce CRM · 2 field changes\", [new ApprovalRowNode(\"Close date\", \"30 Oct 2026\", \"15 Nov 2026\"), new ApprovalRowNode(\"Amount\", \"450,000 NOK\", \"480,000 NOK\")], SystemName: \"Salesforce\");")
        { Icon = "git-compare" },
        new(
            "app-install-card",
            "chat-lfp",
            "AppInstallCard (LFP)",
            "Verified Smart App manifest card with capability review and 1-click install.",
            "IntoChat.UI.AppInstallCard@1",
            ["AppInstallReviewedSignal"],
            "neuron://local/apps/installer#tripletex-sync",
            "new RecordCardNode(\"Smart App Manifest\", \"Tripletex Accounting Sync (v1.4.0)\", [new RecordFieldNode(\"Publisher\", \"Nordlys Digital (Verified)\"), new RecordFieldNode(\"Capabilities\", \"Files (read), Postgres (read)\")], LinkLabel: \"Review & Install\");")
        { Icon = "package-plus" },
        new(
            "record-card",
            "chat-lfp",
            "RecordCard (LFP)",
            "Two-column entity snapshot card with status and deep-link navigation.",
            "IntoChat.UI.RecordCard@1",
            ["RecordLoadedSignal"],
            "neuron://local/salesforce.crm/opportunities/nordlys-q4",
            "new RecordCardNode(\"Opportunity\", \"Nordlys Q4 Enterprise Renewal\", [new RecordFieldNode(\"Amount\", \"480,000 NOK\"), new RecordFieldNode(\"Close Date\", \"15 Nov 2026\"), new RecordFieldNode(\"Stage\", \"Negotiation / Review\")], Subtitle: \"Kari Holm · Oslo, Norway\");")
        { Icon = "contact" },
        new(
            "secret-card",
            "chat-lfp",
            "SecretCard (LFP)",
            "Zero-leakage credential request card routing secrets directly to Vault.",
            "IntoChat.UI.SecretCard@1",
            ["SecretStoredSignal"],
            "neuron://local/vault/secrets#tripletex-token",
            "new SecretCardNode(\"Tripletex Session Token\", \"Required to verify Q4 invoice ledger balances in Tripletex.\", SavedIn: \"Secure Vault · neuron://local/vault/secrets\");")
        { Icon = "key-round" },
        new(
            "job-card",
            "chat-lfp",
            "JobCard (LFP)",
            "Multi-step background workflow progress card with live step status.",
            "IntoChat.UI.JobCard@1",
            ["JobStepAdvancedSignal"],
            "neuron://local/runtime/jobs#bdd-gate",
            "new JobCardNode(\"job-1\", \"Verifying BDD [Scenario] Gate & Deploying Smart App\", \"running\", 0.75, [new JobStepNode(\"Compile C# App<TState> grain\", \"done\", \"118ms\"), new JobStepNode(\"Execute [Scenario] BDD assertions\", \"done\", \"14/14 green\"), new JobStepNode(\"Publish neuron://local/apps/crm\", \"running\", \"Streaming…\")]);")
        { Icon = "workflow" },
        new(
            "remember-card",
            "chat-lfp",
            "RememberCard (LFP)",
            "Episodic memory confirmation card for saving facts to DigitalBrain.Memory.",
            "DigitalBrain.Memory.IEpisodicMemory@1",
            ["MemoryCommittedSignal"],
            "neuron://local/memory/episodes#nordlys-format",
            "await memory.RememberFact(\"preference\", \"Always format Nordlys renewal amounts in NOK with tabular figures.\", importance: 8);")
        { Icon = "brain" },
        new(
            "file-cards",
            "chat-lfp",
            "FileCards (FileCard, FileChip & SaveToPicker) (LFP)",
            "Artifact write cards, hover path chips, and canonical neuron:// file references.",
            "IntoChat.OS.Storage.IFilesWorkspace@1",
            ["FileSavedSignal"],
            "neuron://nordlys/storage.files/30-projects/nordlys-q4-renewal.md",
            "new RecordCardNode(\"Files Vault\", \"nordlys-q4-renewal.md\", [new RecordFieldNode(\"Path\", \"neuron://nordlys/storage.files/30-projects/nordlys-q4-renewal.md\"), new RecordFieldNode(\"Size\", \"18 KB · 30 Projects › Nordlys AS\")], Subtitle: \"Updated close date to 15 Nov 2026 and attached ledger table.\", LinkLabel: \"Open in Files\");")
        { Icon = "file-check" },
        new(
            "error-line",
            "chat-lfp",
            "ErrorLine (LFP)",
            "Single-line actionable recovery bar for timeouts, rate limits, and auth grants.",
            "IntoChat.OS.Assistant.ISessionDiagnostics@1",
            ["RecoveryTriggeredSignal"],
            "neuron://local/apps/ino#diagnostics",
            "new InfoBar(\"Connection to Salesforce timed out\", \"Nothing was changed yet. Click Retry to reconnect with the capability broker.\", Tone: \"danger\", Action: new Button(\"Retry\", new ButtonAction(\"gallery\", \"select\", [\"pulse\"]), Tone: ButtonTone.Accent) { Small = true });")
        { Icon = "triangle-alert" },

        // 5. 3D Brain & Neurons ("3d-brain")
        new(
            "brain-scene-3d",
            "3d-brain",
            "BrainScene3D Orbital Graph",
            "Interactive 3D orbital projection of active neurons, modules, and traveling [Signal] photon pulses.",
            "IntoChat.UI.BrainScene@1",
            ["creator.inspect", "creator.prompt"],
            "neuron://local/creator/brain-scene#orbital",
            "await Inspect(new CreatorInspect(\"OperatingSystem\"));")
        { Icon = "brain-circuit" },
        new(
            "neuron-paths",
            "3d-brain",
            "Distributed NeuronId Addressing",
            "Canonical neuron:// URIs across Files, UI controls, and cross-brain Circle grants.",
            "DigitalBrain.NeuronId@1",
            ["SynapseRoutedSignal"],
            "neuron://nordlys/storage.files/30-projects/nordlys-as/context.md",
            "new NeuronRef(\"files\", \"neuron://nordlys/storage.files/30-projects/nordlys-as/context.md\");")
        { Icon = "network" },
    ];
}

[DigitalBrain.App("gallery", Accent = "#22C55E", Category = "Studio")]
public sealed class GalleryApp : DigitalBrain.App<GalleryAppState>
{
    public static DigitalBrain.AppDefinition Definition { get; } = new(typeof(GalleryApp));

    public static AppBundle Bundle { get; } = new(
        "gallery",
        "1.0.0",
        new AppManifest(
            Title: "UI Gallery",
            Description: "WinUI 3-inspired UI Kit, NodeContract inspector, and UI Identity switcher.",
            Operations:
            [
                new("open", "Open the UI Kit gallery catalog."),
                new("filter", "Filter controls by category or search query."),
                new("select", "Inspect a control's NodeContract, [Signal]s, and neuron:// URI."),
                new("switch-identity", "Switch the active StylePreset and BarLayout."),
            ],
            Settings: [],
            Slots: [],
            Icon: "widgets",
            Toolbar:
            [
                new("open", Icon: "widgets", Label: "Catalog"),
                new("switch-identity", Icon: "palette", Label: "Switch Identity"),
            ]),
        "",
        new Dictionary<string, string>(StringComparer.Ordinal));

    [DigitalBrain.On]
    public async Task Open(GalleryOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var category = string.IsNullOrWhiteSpace(request.Category) ? "all" : NormalizeCategory(request.Category);
        var preset = State.StylePreset;
        var barLayout = State.BarLayout;
        if (TryGetSettings() is { } settings)
        {
            var current = await settings.Read();
            preset = current.StylePreset;
            barLayout = current.BarLayout;
        }

        Set(State with
        {
            Category = category,
            StylePreset = preset,
            BarLayout = barLayout,
            Status = $"Showing {FilteredEntries(category, State.Search).Length} UI Kit controls ({preset} · {barLayout})",
        });
    }

    [DigitalBrain.On]
    public void Filter(GalleryFilter request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var rawCategory = request.Category?.Trim();
        var viewMode = State.ViewMode;
        if (string.Equals(rawCategory, "home", StringComparison.OrdinalIgnoreCase)
            || string.Equals(rawCategory, "grid", StringComparison.OrdinalIgnoreCase))
        {
            rawCategory = "all";
            viewMode = "grid";
        }
        else if (rawCategory is not null && rawCategory.StartsWith("grid:", StringComparison.OrdinalIgnoreCase))
        {
            rawCategory = rawCategory["grid:".Length..];
            viewMode = "grid";
        }

        var category = string.IsNullOrWhiteSpace(rawCategory) ? State.Category : NormalizeCategory(rawCategory);
        var search = request.Search is null ? State.Search : request.Search.Trim();
        var matches = FilteredEntries(category, search);
        var selected = matches.Any(e => e.Id == State.SelectedId)
            ? State.SelectedId
            : matches.FirstOrDefault()?.Id ?? State.SelectedId;
        Set(State with
        {
            Category = category,
            Search = search,
            SelectedId = selected,
            ViewMode = viewMode,
            Status = $"Filtered {matches.Length} control(s) in '{category}'",
        });
    }

    [DigitalBrain.On]
    public void Select(GallerySelect request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var raw = string.IsNullOrWhiteSpace(request.ControlId) ? "approval-card" : request.ControlId.Trim();

        if (string.Equals(raw, "home", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "grid", StringComparison.OrdinalIgnoreCase))
        {
            Set(State with
            {
                ViewMode = "grid",
                Status = $"Catalog Grid · {FilteredEntries(State.Category, State.Search).Length} controls",
            });
            return;
        }

        if (string.Equals(raw, "detail", StringComparison.OrdinalIgnoreCase))
        {
            Set(State with
            {
                ViewMode = "detail",
                Status = $"Inspecting {State.SelectedId}",
            });
            return;
        }

        if (string.Equals(raw, "pulse", StringComparison.OrdinalIgnoreCase))
        {
            var nextPulse = State.SignalPulseCount + 1;
            var currentEntry = State.Entries.FirstOrDefault(e => e.Id == State.SelectedId) ?? State.Entries[0];
            Set(State with
            {
                SignalPulseCount = nextPulse,
                Status = $"Fired [Signal] {currentEntry.Signals.FirstOrDefault() ?? "gallery.select"} (#{nextPulse})",
            });
            return;
        }

        if (raw.StartsWith("tab:", StringComparison.OrdinalIgnoreCase))
        {
            var tab = raw["tab:".Length..].Trim().ToLowerInvariant();
            Set(State with
            {
                CodeTab = tab is "flutter" or "contract" ? tab : "csharp",
                Status = $"Code Inspector tab: {tab.ToUpperInvariant()}",
            });
            return;
        }

        if (raw.StartsWith("knob:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = raw.Split(':', 3, StringSplitOptions.TrimEntries);
            var knobKind = parts.ElementAtOrDefault(1)?.ToLowerInvariant() ?? "";
            var knobVal = parts.ElementAtOrDefault(2)?.ToLowerInvariant() ?? "";
            var nextState = knobKind switch
            {
                "style" => State with { KnobStyle = knobVal, Status = $"Property Knob · Style = {knobVal}" },
                "tone" => State with { KnobTone = knobVal, Status = $"Property Knob · Tone = {knobVal}" },
                "size" => State with { KnobSmall = knobVal is "small" or "true", Status = $"Property Knob · Size = {(knobVal is "small" or "true" ? "Small (30px)" : "Standard (36px)")}" },
                "state" => State with { KnobState = knobVal, Status = $"Property Knob · State = {knobVal}" },
                "surface" => State with { KnobSurface = knobVal, Status = $"Property Knob · Surface = {knobVal}" },
                "chart" => State with { KnobChartKind = knobVal, Status = $"Property Knob · ChartKind = {knobVal}" },
                _ => State,
            };
            Set(nextState);
            return;
        }

        if (raw.StartsWith("toggle:", StringComparison.OrdinalIgnoreCase))
        {
            var target = raw["toggle:".Length..].Trim().ToLowerInvariant();
            Set(target switch
            {
                "lock" => State with { SwitchLocked = !State.SwitchLocked, Status = State.SwitchLocked ? "Unlocked switch policy" : "Locked switch policy" },
                _ => State with { SwitchValue = !State.SwitchValue, Status = !State.SwitchValue ? "Live [Signal] stream active" : "Stream paused" },
            });
            return;
        }

        if (raw.StartsWith("option:", StringComparison.OrdinalIgnoreCase))
        {
            var opt = raw["option:".Length..].Trim();
            Set(State with { SelectedOption = opt, Status = $"Selected option: {opt}" });
            return;
        }

        if (raw.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
        {
            var nav = raw["shell:".Length..].Trim().ToLowerInvariant();
            Set(State with { ShellNav = nav, Status = $"AppShell pane: {nav}" });
            return;
        }

        if (raw.StartsWith("card:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = raw.Split(':', 3, StringSplitOptions.TrimEntries);
            var cardKind = parts.ElementAtOrDefault(1)?.ToLowerInvariant() ?? "";
            var cardAct = parts.ElementAtOrDefault(2)?.ToLowerInvariant() ?? "";
            Set(cardKind switch
            {
                "approval" => State with { ApprovalStatus = cardAct, SignalPulseCount = State.SignalPulseCount + 1, Status = $"ApprovalCard: {cardAct}" },
                "choice" => State with { ChoicePickedIndex = int.TryParse(cardAct, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx) ? idx : 0, SignalPulseCount = State.SignalPulseCount + 1, Status = $"ChoiceCard picked option #{cardAct}" },
                "draft" => State with { DraftStatus = cardAct, SignalPulseCount = State.SignalPulseCount + 1, Status = $"DraftCard: {cardAct}" },
                "secret" => State with { SecretStatus = cardAct, SignalPulseCount = State.SignalPulseCount + 1, Status = $"SecretCard: {cardAct}" },
                "job" => State with { JobStatus = cardAct, SignalPulseCount = State.SignalPulseCount + 1, Status = $"JobCard: {cardAct}" },
                _ => State,
            });
            return;
        }

        if (raw.StartsWith("node:", StringComparison.OrdinalIgnoreCase))
        {
            var nodeId = raw["node:".Length..].Trim();
            Set(State with { SelectedBrainNode = nodeId, Status = $"Inspecting 3D Brain node: {nodeId}" });
            return;
        }

        var normalizedId = raw.Equals("brain-3d", StringComparison.OrdinalIgnoreCase)
            ? "brain-scene-3d"
            : raw.Equals("acrylic", StringComparison.OrdinalIgnoreCase)
                ? "acrylic-surface"
                : raw;

        var match = State.Entries.FirstOrDefault(e => string.Equals(e.Id, normalizedId, StringComparison.OrdinalIgnoreCase))
            ?? State.Entries[0];
        Set(State with
        {
            SelectedId = match.Id,
            ViewMode = "detail",
            Status = $"Inspecting {match.Title} ({match.NeuronContract})",
        });
    }

    [DigitalBrain.On]
    public async Task SwitchIdentity(GallerySwitchIdentity request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var preset = string.IsNullOrWhiteSpace(request.StylePreset) ? State.StylePreset : request.StylePreset.Trim();
        var barLayout = string.IsNullOrWhiteSpace(request.BarLayout) ? DefaultBarLayoutFor(preset, State.BarLayout) : request.BarLayout.Trim();
        if (TryGetSettings() is { } settings)
        {
            var saved = await settings.SetUiIdentity(preset, barLayout);
            preset = saved.StylePreset;
            barLayout = saved.BarLayout;
        }

        Set(State with
        {
            StylePreset = preset,
            BarLayout = barLayout,
            Status = $"Active UI Identity: {preset} ({barLayout})",
        });
    }

    public View RenderSide()
    {
        var visible = FilteredEntries(State.Category, State.Search);
        var designCount = State.Entries.Count(e => e.Category == "design");
        var controlsCount = State.Entries.Count(e => e.Category == "controls");
        var layoutCount = State.Entries.Count(e => e.Category == "layout");
        var lfpCount = State.Entries.Count(e => e.Category == "chat-lfp");
        var brainCount = State.Entries.Count(e => e.Category == "3d-brain");

        var categoryButtons = new Node[]
        {
            Ui.NavItem("cat-home", "Catalog Grid (Home)", "house", "select:home", selected: State.ViewMode == "grid", badge: $"{State.Entries.Length}"),
            Ui.NavItem("cat-all", "All Controls", "layout-grid", "filter:all", selected: State.Category == "all" && State.ViewMode != "grid", badge: $"{State.Entries.Length}"),
            Ui.NavItem("cat-design", "Design & Identity", "palette", "filter:design", selected: State.Category == "design", badge: $"{designCount}"),
            Ui.NavItem("cat-controls", "Basic Input", "mouse-pointer-click", "filter:controls", selected: State.Category == "controls", badge: $"{controlsCount}"),
            Ui.NavItem("cat-layout", "Layout & App Shell", "panel-left", "filter:layout", selected: State.Category == "layout", badge: $"{layoutCount}"),
            Ui.NavItem("cat-lfp", "Data & 13 LFP Chat Cards", "table", "filter:chat-lfp", selected: State.Category == "chat-lfp", badge: $"{lfpCount}"),
            Ui.NavItem("cat-3d", "3D Brain & Neurons", "brain-circuit", "filter:3d-brain", selected: State.Category == "3d-brain", badge: $"{brainCount}"),
        };

        var entryNavItems = visible
            .Select(entry => (Node)Ui.NavItem(
                $"sample-{entry.Id}",
                entry.Title,
                entry.Icon,
                $"select:{entry.Id}",
                selected: State.ViewMode != "grid" && string.Equals(State.SelectedId, entry.Id, StringComparison.OrdinalIgnoreCase),
                badge: CategoryShortBadge(entry.Category)))
            .ToArray();

        return new(
            "UI Kit Catalog",
            new Column(
            [
                new Surface(
                    [
                        new Row(
                        [
                            new TagPill(State.StylePreset, "accent", "palette"),
                            new TagPill(State.BarLayout, "neutral", "layout"),
                        ]) { Gap = 6 },
                        new Text($"Identity: {State.StylePreset} · {State.BarLayout}") { Tone = "muted", Tabular = true },
                    ],
                    Kind: SurfaceKind.Acrylic,
                    Title: "UI Gallery · Component Studio"),
                new TextField("gallery-search", State.Search, "Search 5 categories & LFP cards…")
                {
                    OnSubmit = new ButtonAction("gallery", "filter", [State.Category, State.Search]),
                },
                new Divider("CATEGORIES (5)"),
                new Column(categoryButtons) { Gap = 4 },
                new Divider($"CONTROLS ({visible.Length})"),
                new Column(entryNavItems) { Gap = 4 },
            ])
            { Gap = 10 });
    }

    public View Render()
    {
        var selected = State.Entries.FirstOrDefault(e => e.Id == State.SelectedId) ?? State.Entries[0];
        var visible = FilteredEntries(State.Category, State.Search);

        if (string.Equals(State.ViewMode, "grid", StringComparison.OrdinalIgnoreCase))
        {
            return new(
                "UI Gallery",
                new Column(
                [
                    RenderStudioHeroBanner(selected),
                    RenderCatalogGridSection(visible),
                ])
                { Gap = 16 });
        }

        var livePreview = BuildSamplePreviewNode(selected);
        var stageSurfaceKind = State.KnobSurface switch
        {
            "recessed" => SurfaceKind.Recessed,
            "acrylic" => SurfaceKind.Acrylic,
            _ => SurfaceKind.Elevated,
        };

        return new(
            "UI Gallery",
            new Column(
            [
                new Surface(
                    [
                        new Row(
                        [
                            new Button("Back to Gallery", new ButtonAction("gallery", "select", ["home"]), Tone: ButtonTone.Neutral)
                            {
                                Icon = "arrow-left",
                                Small = true,
                            },
                            new TagPill(CategoryDisplayName(selected.Category), "accent", selected.Icon),
                            new TagPill(selected.NeuronContract, "ok", "cpu"),
                            new TagPill($"[Signal] {selected.Signals.FirstOrDefault() ?? "gallery.select"} ({State.SignalPulseCount})", "done", "zap"),
                            new TagPill($"{State.StylePreset} · {State.BarLayout}", "neutral", "palette"),
                        ]) { Gap = 8 },
                        new Heading(selected.Title, Level: 1, Subtitle: selected.Summary),
                        new Text(State.Status) { Tone = "muted", Tabular = true },
                        new Text($"Control: {selected.Title} [{selected.NeuronContract}]") { Tabular = true },
                        new Text($"URI: {selected.SourceNeuronUri}") { Mono = true, Tabular = true },
                        new Text($"Signals: {string.Join(", ", selected.Signals)}") { Mono = true },
                        new NeuronRef("gallery", selected.SourceNeuronUri),
                    ],
                    Kind: SurfaceKind.Acrylic,
                    Title: "UI Gallery · Interactive Control Playground"),
                new SplitPane(
                    new Surface(
                        [
                            livePreview,
                        ],
                        Kind: stageSurfaceKind,
                        Title: "Live Control Preview",
                        Subtitle: $"{selected.NeuronContract} · State: {State.KnobState} · Surface: {State.KnobSurface}"),
                    RenderPropertyKnobsSurface(),
                    LeadingExtent: 540,
                    Gap: 14),
                RenderCodeAndIdentityInspector(selected),
            ])
            { Gap = 16 });
    }

    private Node RenderStudioHeroBanner(GalleryCatalogEntry selected) => new Surface(
        [
            new Heading(
                "IntoChat UI Gallery · Single-Format Component Studio",
                Level: 1,
                Subtitle: "7 UI Identities · Squircle IcAppIcon · Ergonomic C# DigitalBrain.UI Builders · 13 Interactive LFP Chat Cards · 3D Neural Graph"),
            new Row(
            [
                new TagPill($"{State.Entries.Length} Components", "accent", "sparkles"),
                new TagPill("13 LFP Chat Cards", "done", "table"),
                new TagPill($"{State.StylePreset} · {State.BarLayout}", "neutral", "palette"),
                new Button("Inspect Selected Control", new ButtonAction("gallery", "select", [selected.Id]), Tone: ButtonTone.Accent)
                {
                    Icon = "arrow-up-right",
                    Small = true,
                },
            ]) { Gap = 8 },
            RenderIdentityPresetRow(),
            new Text(State.Status) { Tone = "muted", Tabular = true },
            new Text($"Control: {selected.Title} [{selected.NeuronContract}] · {selected.SourceNeuronUri}") { Mono = true, Tabular = true, Tone = "subtle" },
        ],
        Kind: SurfaceKind.Acrylic,
        Title: "UI Gallery · WinUI 3 Component Studio");

    private Node RenderPropertyKnobsSurface() => new Surface(
        [
            new Text("STYLE (Primary · Secondary · Ghost · Danger)") { Tone = "muted", Size = 11 },
            new Row(
            [
                KnobButton("Primary", "knob:style:primary", State.KnobStyle == "primary"),
                KnobButton("Secondary", "knob:style:secondary", State.KnobStyle == "secondary"),
                KnobButton("Ghost", "knob:style:ghost", State.KnobStyle == "ghost"),
                KnobButton("Danger", "knob:style:danger", State.KnobStyle == "danger"),
            ]) { Gap = 6 },
            new Text("TONE (Neutral · Accent · Done · Working · Error)") { Tone = "muted", Size = 11 },
            new Row(
            [
                KnobButton("Neutral", "knob:tone:neutral", State.KnobTone == "neutral"),
                KnobButton("Accent", "knob:tone:accent", State.KnobTone == "accent"),
                KnobButton("Done", "knob:tone:done", State.KnobTone == "done"),
                KnobButton("Working", "knob:tone:working", State.KnobTone == "working"),
                KnobButton("Error", "knob:tone:error", State.KnobTone == "error"),
            ]) { Gap = 6 },
            new Text("SIZE (Standard 36px · Small 30px)") { Tone = "muted", Size = 11 },
            new Row(
            [
                KnobButton("Standard (36px)", "knob:size:standard", !State.KnobSmall),
                KnobButton("Small (30px)", "knob:size:small", State.KnobSmall),
            ]) { Gap = 6 },
            new Text("STATE (Interactive · Loading · Disabled · Empty)") { Tone = "muted", Size = 11 },
            new Row(
            [
                KnobButton("Interactive", "knob:state:interactive", State.KnobState == "interactive"),
                KnobButton("Loading", "knob:state:loading", State.KnobState == "loading"),
                KnobButton("Disabled", "knob:state:disabled", State.KnobState == "disabled"),
                KnobButton("Empty", "knob:state:empty", State.KnobState == "empty"),
            ]) { Gap = 6 },
            new Text("SURFACE (Acrylic · Elevated · Recessed)") { Tone = "muted", Size = 11 },
            new Row(
            [
                KnobButton("Acrylic", "knob:surface:acrylic", State.KnobSurface == "acrylic"),
                KnobButton("Elevated", "knob:surface:elevated", State.KnobSurface == "elevated"),
                KnobButton("Recessed", "knob:surface:recessed", State.KnobSurface == "recessed"),
            ]) { Gap = 6 },
            new Text("CHART KIND (Bar · Line)") { Tone = "muted", Size = 11 },
            new Row(
            [
                KnobButton("Bar", "knob:chart:bar", State.KnobChartKind == "bar"),
                KnobButton("Line", "knob:chart:line", State.KnobChartKind == "line"),
            ]) { Gap = 6 },
        ],
        Kind: SurfaceKind.Recessed,
        Title: "Property Knobs",
        Subtitle: "Live C# state mutation");

    private Node RenderCodeAndIdentityInspector(GalleryCatalogEntry selected)
    {
        var dynamicSnippet = BuildDynamicCodeSnippet(selected);
        return new Surface(
            [
                RenderIdentityPresetRow(),
                new Row(
                [
                    new Button("C# DigitalBrain.UI", new ButtonAction("gallery", "select", ["tab:csharp"]), Tone: State.CodeTab == "csharp" ? ButtonTone.Accent : ButtonTone.Neutral)
                    {
                        Icon = "code",
                        Small = true,
                    },
                    new Button("Flutter Ic*", new ButtonAction("gallery", "select", ["tab:flutter"]), Tone: State.CodeTab == "flutter" ? ButtonTone.Accent : ButtonTone.Neutral)
                    {
                        Icon = "layers",
                        Small = true,
                    },
                    new Button("Contract & [Signal]", new ButtonAction("gallery", "select", ["tab:contract"]), Tone: State.CodeTab == "contract" ? ButtonTone.Accent : ButtonTone.Neutral)
                    {
                        Icon = "radio",
                        Small = true,
                    },
                    new Button("Fire Test [Signal]", new ButtonAction("gallery", "select", ["pulse"]), Tone: ButtonTone.Accent)
                    {
                        Icon = "zap",
                        Small = true,
                    },
                    new Button("Copy Snippet", new ButtonAction("gallery", "copy", [dynamicSnippet]), Tone: ButtonTone.Neutral)
                    {
                        Icon = "copy",
                        Small = true,
                    },
                ]) { Gap = 8 },
                new Surface(
                    [
                        new Text(dynamicSnippet) { Mono = true, Tabular = true },
                    ],
                    Kind: SurfaceKind.Acrylic),
            ],
            Kind: SurfaceKind.Recessed,
            Title: "C# App<TState> & [Signal] Contract",
            Subtitle: $"{selected.NeuronContract} · {selected.SourceNeuronUri}");
    }

    private Node RenderIdentityPresetRow() => new Row(
    [
        new Button("Fluent Acrylic", new ButtonAction("gallery", "switch-identity", ["fluent-acrylic", "floating-glass"]), Tone: State.StylePreset == "fluent-acrylic" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true, Icon = "palette" },
        new Button("Spatial Glass", new ButtonAction("gallery", "switch-identity", ["spatial-glass", "compact-pill"]), Tone: State.StylePreset == "spatial-glass" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true, Icon = "sparkles" },
        new Button("Cyber Synaptic", new ButtonAction("gallery", "switch-identity", ["cyber-synaptic", "split-top-dock"]), Tone: State.StylePreset == "cyber-synaptic" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true, Icon = "zap" },
        new Button("Warm Paper", new ButtonAction("gallery", "switch-identity", ["warm-paper", "classic-bottom"]), Tone: State.StylePreset == "warm-paper" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true, Icon = "sun" },
        new Button("Warm Mica", new ButtonAction("gallery", "switch-identity", ["fluent-warm-mica", "floating-glass"]), Tone: State.StylePreset == "fluent-warm-mica" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true, Icon = "sun" },
        new Button("Linear Carbon", new ButtonAction("gallery", "switch-identity", ["linear-carbon", "classic-bottom"]), Tone: State.StylePreset == "linear-carbon" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true, Icon = "terminal" },
        new Button("Nordic Tactical", new ButtonAction("gallery", "switch-identity", ["nordic-tactical", "split-top-dock"]), Tone: State.StylePreset == "nordic-tactical" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true, Icon = "shield" },
    ])
    { Gap = 8 };

    private Node RenderCatalogGridSection(GalleryCatalogEntry[] visible)
    {
        if (visible.Length == 0)
        {
            return new InfoBar(
                "No components found",
                $"No UI Kit components matched '{State.Search}' in category '{State.Category}'.",
                Tone: "warning",
                Action: new Button("Reset Filter", new ButtonAction("gallery", "filter", ["all", ""]), Tone: ButtonTone.Accent) { Small = true });
        }

        var cards = visible
            .Select(entry =>
            {
                var isSelected = string.Equals(entry.Id, State.SelectedId, StringComparison.OrdinalIgnoreCase);
                return (Node)new Surface(
                    [
                        new Row(
                        [
                            new TagPill(CategoryShortBadge(entry.Category), isSelected ? "accent" : "neutral", entry.Icon),
                            new TagPill(entry.Signals.FirstOrDefault() ?? "Signal", "done", "zap"),
                        ]) { Gap = 6 },
                        new Text(entry.Summary) { Tone = "muted", Size = 12 },
                        new Text(entry.NeuronContract) { Mono = true, Tone = "subtle", Size = 11.5 },
                        new Button(
                            $"Inspect {entry.Title}",
                            new ButtonAction("gallery", "select", [entry.Id]),
                            Tone: isSelected ? ButtonTone.Accent : ButtonTone.Neutral)
                        {
                            Icon = isSelected ? "check" : "arrow-up-right",
                            Small = true,
                        },
                    ],
                    Kind: isSelected ? SurfaceKind.Elevated : SurfaceKind.Acrylic,
                    Title: entry.Title,
                    Subtitle: CategoryDisplayName(entry.Category));
            })
            .ToArray();

        return new Surface(
            [
                new Grid(3, cards, Gap: 12),
            ],
            Kind: SurfaceKind.Acrylic,
            Title: $"Component Catalog Grid ({visible.Length} of {State.Entries.Length})",
            Subtitle: "Click any component card to inspect its live neuron preview and contract");
    }

    private Node BuildSamplePreviewNode(GalleryCatalogEntry selected)
    {
        var enabled = State.KnobState != "disabled";
        if (State.KnobState == "loading")
        {
            return new Column(
            [
                new Progress($"Loading {selected.Title}…", 0.65, Detail: selected.NeuronContract),
                new InfoBar($"Streaming {selected.NeuronContract}", $"Awaiting grain projection from {selected.SourceNeuronUri}…", Tone: "info"),
            ])
            { Gap = 12 };
        }

        if (State.KnobState == "empty")
        {
            return new InfoBar(
                $"Empty state for {selected.Title}",
                $"No active items projected at {selected.SourceNeuronUri}. Switch the State knob back to Interactive to populate.",
                Tone: "warning",
                Action: new Button("Restore Interactive", new ButtonAction("gallery", "select", ["knob:state:interactive"]), Tone: ButtonTone.Accent) { Small = true });
        }

        var badgeTone = MapKnobBadgeTone(State.KnobTone);
        var btnTone = State.KnobStyle == "primary"
            ? ButtonTone.Accent
            : ButtonTone.Neutral;

        return selected.Id switch
        {
            "themes" => new Column(
            [
                new Text($"Active Identity: {State.StylePreset} · BarLayout: {State.BarLayout}") { Tone = "strong", Tabular = true },
                RenderIdentityPresetRow(),
                new Grid(
                    3,
                    [
                        Ui.StatCard("theme-blur", "Backdrop Blur", State.StylePreset == "warm-paper" ? "0px (Matte)" : "24px Sigma", "Glass", badgeTone, "layers"),
                        Ui.StatCard("theme-dock", "Dock Topology", State.BarLayout, "Adaptive", BadgeTone.Accent, "layout"),
                        Ui.StatCard("theme-shader", "3D Brain Shader", State.StylePreset, "60 FPS", BadgeTone.Done, "brain-circuit"),
                    ],
                    Gap: 10),
            ])
            { Gap = 12 },

            "typography" => new Column(
            [
                new Heading("InterDisplay 32 · 1,492,048 tok", Level: 1, Subtitle: "Inter Body 14 · Tabular numerals (tnum) keep live telemetry aligned across frames."),
                new Text("neuron://nordlys/apps.ino/main · 99.98% SLA · 12.4ms p95 · 142 sig/min") { Mono = true, Tabular = true, Tone = "accent" },
                new Row(
                [
                    new TagPill("Inter Regular 400", "neutral", "type"),
                    new TagPill("Inter SemiBold 600", "accent", "type"),
                    new TagPill("InterDisplay Bold 700 (tnum)", "done", "sparkles"),
                ]) { Gap = 8 },
            ])
            { Gap = 10 },

            "iconography" => new Grid(
                3,
                [
                    Ui.StatCard("ico-ino", "Ino Assistant", "#3D7EFF", "bot", BadgeTone.Accent, "sparkles"),
                    Ui.StatCard("ico-creator", "Creator Studio", "#FF5D8F", "brain-circuit", BadgeTone.Done, "brain-circuit"),
                    Ui.StatCard("ico-files", "Files Finder", "#F6B73C", "folder", BadgeTone.Accent, "folder"),
                    Ui.StatCard("ico-gallery", "UI Gallery", "#22C55E", "widgets", BadgeTone.Done, "palette"),
                    Ui.StatCard("ico-photocraft", "PhotoCraft", "#EC4899", "image", BadgeTone.Working, "image"),
                    Ui.StatCard("ico-settings", "Settings", "#64748B", "settings", BadgeTone.Neutral, "settings"),
                ],
                Gap: 10),

            "acrylic-surface" or "surfaces" => new Grid(
                3,
                [
                    new Surface(
                        [
                            new Text("24px backdrop blur + 1px top-lit specular hairline border.") { Tone = "muted" },
                            new TagPill("SurfaceKind.Acrylic", State.KnobTone, "layers"),
                        ],
                        Kind: SurfaceKind.Acrylic,
                        Title: "Acrylic Surface",
                        Subtitle: $"Preset: {State.StylePreset}"),
                    new Surface(
                        [
                            new Text("Raised modal & card container with ambient drop shadow.") { Tone = "muted" },
                            new TagPill("SurfaceKind.Elevated", "accent", "square-stack"),
                        ],
                        Kind: SurfaceKind.Elevated,
                        Title: "Elevated Surface",
                        Subtitle: "14px Card Radius"),
                    new Surface(
                        [
                            new Text("Sunk inspector well for code snippets, logs, and property knobs.") { Tone = "muted" },
                            new TagPill("SurfaceKind.Recessed", "neutral", "terminal"),
                        ],
                        Kind: SurfaceKind.Recessed,
                        Title: "Recessed Surface",
                        Subtitle: "1px Hairline Border"),
                ],
                Gap: 10),

            "buttons" => new Column(
            [
                new Row(
                [
                    new Button($"Active Knob ({State.KnobStyle})", new ButtonAction("gallery", "select", ["pulse"]), Tone: btnTone)
                    {
                        Style = State.KnobStyle,
                        Small = State.KnobSmall,
                        Enabled = enabled,
                        Icon = "sparkles",
                    },
                    new Button("Primary", new ButtonAction("gallery", "select", ["pulse"]), Tone: ButtonTone.Accent)
                    {
                        Style = "primary",
                        Small = State.KnobSmall,
                        Enabled = enabled,
                        Icon = "rocket",
                    },
                    new Button("Secondary", new ButtonAction("gallery", "select", ["pulse"]), Tone: ButtonTone.Neutral)
                    {
                        Style = "secondary",
                        Small = State.KnobSmall,
                        Enabled = enabled,
                        Icon = "sliders-horizontal",
                    },
                    new Button("Ghost", new ButtonAction("gallery", "select", ["pulse"]), Tone: ButtonTone.Neutral)
                    {
                        Style = "ghost",
                        Small = State.KnobSmall,
                        Enabled = enabled,
                        Icon = "eye",
                    },
                    new Button("Danger", new ButtonAction("gallery", "select", ["pulse"]), Tone: ButtonTone.Neutral)
                    {
                        Style = "danger",
                        Small = State.KnobSmall,
                        Enabled = enabled,
                        Icon = "trash",
                    },
                ]) { Gap = 10 },
                new Text($"Height: {(State.KnobSmall ? "30px (Small)" : "36px (Standard)")} · Enabled: {enabled} · Pulses fired: {State.SignalPulseCount}") { Tone = "muted", Tabular = true },
            ])
            { Gap = 12 },

            "inputs" => new Column(
            [
                new TextField("neuron-address", "neuron://nordlys/apps.ino/main", "Enter neuron:// address…")
                {
                    OnSubmit = new ButtonAction("gallery", "select", ["pulse"]),
                },
                new NeuronRef("ino", "neuron://nordlys/apps.ino/main"),
            ])
            { Gap = 12 },

            "switch" => new Row(
            [
                new Switch(
                    State.SwitchValue ? "Live [Signal] stream active" : "Stream paused",
                    IsOn: State.SwitchValue,
                    Action: enabled ? new ButtonAction("gallery", "select", ["toggle:switch"]) : null,
                    Locked: State.SwitchLocked),
                new Button(
                    State.SwitchLocked ? "Unlock policy" : "Lock policy",
                    new ButtonAction("gallery", "select", ["toggle:lock"]),
                    Tone: State.SwitchLocked ? ButtonTone.Accent : ButtonTone.Neutral)
                {
                    Icon = "lock",
                    Small = true,
                },
            ])
            { Gap = 12 },

            "select" => new Select(
                "Active Reasoning Model",
                State.SelectedOption,
                [
                    new SelectOption("claude-sonnet-4-5", "Claude Sonnet 4.5", new ButtonAction("gallery", "select", ["option:claude-sonnet-4-5"])),
                    new SelectOption("gpt-5-thinking", "GPT-5 Thinking", new ButtonAction("gallery", "select", ["option:gpt-5-thinking"])),
                    new SelectOption("gemini-2.5-pro", "Gemini 2.5 Pro", new ButtonAction("gallery", "select", ["option:gemini-2.5-pro"])),
                    new SelectOption("qwen3.5:4b", "Qwen 3.5 4B (Local)", new ButtonAction("gallery", "select", ["option:qwen3.5:4b"])),
                ]),

            "tag-pill" => new Row(
            [
                new TagPill("NEUTRAL BADGE", "neutral", "sparkles"),
                new TagPill("ACCENT BADGE", "accent", "zap"),
                new TagPill("DONE BADGE", "done", "check"),
                new TagPill("WORKING BADGE", "working", "activity"),
                new TagPill("ERROR BADGE", "error", "triangle-alert"),
            ])
            { Gap = 8 },

            "progress-infobar" => new Column(
            [
                new Progress("Orleans Silo Grain Warmup", 0.72, Detail: "14 / 14 [Scenario] gates green (72%)"),
                new InfoBar(
                    "Orleans grain pool synced 14/14 [Scenario] gates.",
                    "Zero-leakage capability broker verified all active neuron bindings.",
                    Tone: State.KnobTone switch
                    {
                        "error" => "danger",
                        "working" => "warning",
                        "done" => "success",
                        _ => "info",
                    },
                    Action: new Button("Pulse Gate", new ButtonAction("gallery", "select", ["pulse"]), Tone: ButtonTone.Accent) { Small = true, Icon = "zap" }),
            ])
            { Gap = 12 },

            "app-shell" => Ui.AppShell(
                "gallery-shell-preview",
                sidebar: new Column(
                [
                    Ui.NavItem("nav-1", "Overview", "layout-grid", "select:shell:overview", selected: State.ShellNav == "overview"),
                    Ui.NavItem("nav-2", "Signals", "radio", "select:shell:signals", selected: State.ShellNav == "signals", badge: $"{14 + State.SignalPulseCount}"),
                    Ui.NavItem("nav-3", "Contracts", "cpu", "select:shell:contracts", selected: State.ShellNav == "contracts"),
                ])
                { Gap = 6 },
                body: new Column(
                [
                    Ui.StatCard("stat-1", "Active Signals / min", $"{142 + State.SignalPulseCount}", "+18.4%", badgeTone, "zap"),
                    new Text($"Active AppShell pane: {State.ShellNav.ToUpperInvariant()}") { Tone = "muted", Tabular = true },
                ])
                { Gap = 10 },
                toolbar: new Row(
                [
                    new TagPill("DigitalBrain.UI AppShell", "accent", "panel-left"),
                    new TagPill($"Pane: {State.ShellNav}", "done"),
                ])
                { Gap = 8 }),

            "stat-card" => new Grid(
                3,
                [
                    Ui.StatCard("kpi-signals", "Active Signals / min", $"{142 + State.SignalPulseCount}", "+18.4%", badgeTone, "zap"),
                    Ui.StatCard("kpi-tokens", "Compute Throughput", "1,248,920 tok", "+14.2%", BadgeTone.Done, "cpu"),
                    Ui.StatCard("kpi-latency", "P95 Silo Latency", "6.8 ms", "-1.2 ms", BadgeTone.Accent, "activity"),
                ],
                Gap: 10),

            "tabs" => new Column(
            [
                new Tabs(
                    [
                        new TabItem("overview", "OVERVIEW", new ButtonAction("gallery", "select", ["shell:overview"])),
                        new TabItem("signals", "TELEMETRY", new ButtonAction("gallery", "select", ["shell:signals"])),
                        new TabItem("contracts", "CONTRACTS", new ButtonAction("gallery", "select", ["shell:contracts"])),
                    ],
                    State.ShellNav),
                new Surface(
                    [
                        new Text($"Active tab pane: {State.ShellNav} (tnum {1024 + State.SignalPulseCount} signals/s)") { Tone = "strong", Tabular = true },
                    ],
                    Kind: SurfaceKind.Recessed),
            ])
            { Gap = 12 },

            "approval-card" => new ApprovalCardNode(
                "card-gallery-1",
                "Update close date on Nordlys Q4 renewal to 15 Nov 2026?",
                "Ino matched Kari Holm's email to your open Salesforce opportunity (scope:neuron://local/apps/ino#card-1)",
                [
                    new ApprovalRowNode("Nordlys Q4 Enterprise", "30 Oct 2026", "15 Nov 2026", "480,000 NOK"),
                    new ApprovalRowNode("Owner", "Unassigned", "Vlad Horb"),
                ],
                SystemName: "Salesforce",
                Status: State.ApprovalStatus,
                OnApprove: new ButtonAction("gallery", "select", ["card:approval:approved"]),
                OnReject: new ButtonAction("gallery", "select", ["card:approval:rejected"]),
                SourceNeuronUri: selected.SourceNeuronUri),

            "table-card" => new DataTable(
                "Opportunities — Q4 2026",
                ["Account", "Stage", "Close Date", "Amount (NOK)", "Owner"],
                [
                    ["Nordlys Enterprise", "Closed Won", "15 Nov", "480,000", "Vlad"],
                    ["Kystverket Cloud", "Commit", "02 Dec", "920,000", "Aria"],
                    ["Bergen Maritime", "Review", "19 Oct", "310,000", "Devon"],
                ],
                SourceNeuronUri: selected.SourceNeuronUri,
                From: "Postgres"),

            "chart-card" => new Chart(
                State.KnobChartKind == "line" ? "line" : "bar",
                "Won per month, 2026",
                [
                    new ChartPointNode("Jan", 68),
                    new ChartPointNode("Feb", 82),
                    new ChartPointNode("Mar", 94),
                    new ChartPointNode("Apr", 128 + State.SignalPulseCount),
                ],
                ComparePoints: [54, 62, 71, 88],
                SeriesName: "2026",
                CompareName: "2025",
                SourceNeuronUri: selected.SourceNeuronUri,
                From: "Analytics",
                Prefix: "$"),

            "choice-card" => new ChoiceCardNode(
                "choice-gallery-1",
                "Which Nordlys workspace should receive the Q4 renewal brief?",
                [
                    new ChoiceOptionNode("Nordlys Enterprise (Primary)", "neuron://nordlys/circles/enterprise", new ButtonAction("gallery", "select", ["card:choice:0"])),
                    new ChoiceOptionNode("Executive Board Circle", "neuron://nordlys/circles/board", new ButtonAction("gallery", "select", ["card:choice:1"])),
                    new ChoiceOptionNode("Nordic Finance Ops", "neuron://nordlys/circles/finance", new ButtonAction("gallery", "select", ["card:choice:2"])),
                ],
                PickedIndex: State.ChoicePickedIndex),

            "draft-card" => new DraftCardNode(
                "draft-gallery-1",
                "email",
                "jeff@intochat.os",
                "Kari Holm <kari@nordlys.no>",
                "Hi Kari,\nConfirmed: we updated the Q4 renewal close date to 15 Nov 2026 (480,000 NOK).",
                Subject: "Nordlys Q4 Enterprise Renewal — 15 Nov confirmation",
                Status: State.DraftStatus,
                OnSend: new ButtonAction("gallery", "select", ["card:draft:sent"]),
                OnDiscard: new ButtonAction("gallery", "select", ["card:draft:pending"])),

            "change-preview-card" => new ApprovalCardNode(
                "change-gallery-1",
                "Apply 2 CRM field changes on Nordlys Q4 Enterprise",
                "Nordlys Q4 Enterprise",
                [
                    new ApprovalRowNode("Close date", "30 Oct 2026", "15 Nov 2026"),
                    new ApprovalRowNode("Amount", "450,000 NOK", "480,000 NOK"),
                ],
                SystemName: "Salesforce",
                Status: State.ApprovalStatus,
                OnApprove: new ButtonAction("gallery", "select", ["card:approval:approved"]),
                OnReject: new ButtonAction("gallery", "select", ["card:approval:rejected"]),
                SourceNeuronUri: selected.SourceNeuronUri),

            "app-install-card" => new RecordCardNode(
                "Smart App Manifest",
                "Tripletex Accounting Sync (v1.4.0)",
                [
                    new RecordFieldNode("Handle", "nordlys/tripletex-sync"),
                    new RecordFieldNode("Publisher", "Nordlys Digital (Verified)"),
                    new RecordFieldNode("Permissions", "Read Nordlys deals, Render interactive charts, Sync Tripletex ledger"),
                    new RecordFieldNode("Summary", "Synchronizes Norwegian Tripletex invoices with DigitalBrain Files & CRM."),
                ],
                Subtitle: "Verified Smart App package · 1-click capability review",
                LinkLabel: State.ApprovalStatus == "approved" ? "Installed" : "Review & Install",
                OnOpen: new ButtonAction("gallery", "select", ["card:approval:approved"])),

            "record-card" => new RecordCardNode(
                "Opportunity",
                "Nordlys Q4 Enterprise Renewal",
                [
                    new RecordFieldNode("Amount", "480,000 NOK"),
                    new RecordFieldNode("Close Date", "15 Nov 2026"),
                    new RecordFieldNode("Stage", "Negotiation / Review"),
                    new RecordFieldNode("Last Contact", "14 days ago"),
                ],
                Subtitle: "Kari Holm · Oslo, Norway",
                LinkLabel: "Open in Salesforce",
                OnOpen: new ButtonAction("gallery", "select", ["pulse"])),

            "secret-card" => new SecretCardNode(
                "Tripletex Session Token",
                "Required to verify Q4 invoice ledger balances in Tripletex. Stored encrypted in your personal vault.",
                SavedIn: "TRIPLETEX_SESSION_TOKEN",
                Status: State.SecretStatus,
                OnEnter: new ButtonAction("gallery", "select", ["card:secret:saved"]),
                OnSkip: new ButtonAction("gallery", "select", ["card:secret:pending"])),

            "job-card" => new JobCardNode(
                "Verifying BDD [Scenario] Gate & Deploying Smart App",
                [
                    new JobStepNode("Compile C# App<TState> grain", "done", "118ms"),
                    new JobStepNode("Execute [Scenario] BDD assertions", "done", "14/14 green"),
                    new JobStepNode("Publish neuron://local/apps/crm", State.JobStatus == "done" ? "done" : "running", "Streaming…"),
                ],
                Progress: State.JobStatus == "done" ? 1.0 : 0.75,
                Status: State.JobStatus,
                ResultText: "1.4s elapsed",
                OnCancel: new ButtonAction("gallery", "select", [State.JobStatus == "done" ? "card:job:running" : "card:job:done"])),

            "remember-card" => new RecordCardNode(
                "Episodic Memory",
                "Always format Nordlys renewal amounts in NOK with tabular figures.",
                [
                    new RecordFieldNode("Scope", "Nordlys AS · Importance 8/10"),
                ],
                Subtitle: "Nordlys AS",
                LinkLabel: State.ApprovalStatus == "approved" ? "Saved" : "Remember",
                OnOpen: new ButtonAction("gallery", "select", ["card:approval:approved"])),

            "file-cards" => new RecordCardNode(
                "Files Vault",
                "nordlys-q4-renewal.md",
                [
                    new RecordFieldNode("Type", "MD"),
                    new RecordFieldNode("Size", "18 KB"),
                    new RecordFieldNode("Path", "neuron://nordlys/storage.files/30-projects/nordlys-q4-renewal.md"),
                ],
                Subtitle: "30 Projects › Nordlys AS",
                LinkLabel: "Open in Files",
                OnOpen: new ButtonAction("apps/files", "open", [])),

            "error-line" => new InfoBar(
                "Salesforce didn't answer in time — nothing was changed yet",
                "Timeout after 10s on neuron://nordlys/salesforce.crm/q4-pipeline. Click Retry to run the request again.",
                Tone: "danger",
                Action: new Button("Retry Request", new ButtonAction("gallery", "select", ["pulse"]), Tone: ButtonTone.Accent) { Small = true, Icon = "refresh" }),

            "brain-scene-3d" => new BrainSceneNode(
                [
                    new BrainGraphNode("os", "OperatingSystem", "neuron://local/os", "core", 0, 0, 0, "working")
                    {
                        Subtitle = "Orleans Silo Kernel",
                        OnSelect = new ButtonAction("gallery", "select", ["node:os"]),
                    },
                    new BrainGraphNode("ino", "Ino Agent", "neuron://local/apps/ino", "agent", 120, 40, -20, "working")
                    {
                        Subtitle = "Personal AI Coordinator",
                        OnSelect = new ButtonAction("gallery", "select", ["node:ino"]),
                    },
                    new BrainGraphNode("pg", "Postgres", "neuron://local/postgres/main", "connector", -110, -50, 30, "done")
                    {
                        Subtitle = "Nordlys Analytics DB",
                        OnSelect = new ButtonAction("gallery", "select", ["node:pg"]),
                    },
                    new BrainGraphNode("files", "Files Vault", "neuron://local/storage.files", "storage", 85, -80, 60, "done")
                    {
                        Subtitle = "Hybrid Cloud + Local Disk",
                        OnSelect = new ButtonAction("gallery", "select", ["node:files"]),
                    },
                    new BrainGraphNode("creator", "Creator 3D", "neuron://local/apps/creator", "studio", -95, 75, -45, "working")
                    {
                        Subtitle = "Self-Improving Roslyn Studio",
                        OnSelect = new ButtonAction("gallery", "select", ["node:creator"]),
                    },
                ],
                [
                    new BrainGraphSynapse("os", "ino", "InoSend", true),
                    new BrainGraphSynapse("ino", "pg", "InoQueryDb", true),
                    new BrainGraphSynapse("ino", "files", "FileSavedSignal", true),
                    new BrainGraphSynapse("os", "creator", "CreatorInspect", true),
                ],
                SelectedId: State.SelectedBrainNode),

            "files-neuron-uri" or "neuron-paths" => new Column(
            [
                new NeuronRef("files", "neuron://owner/drive.files/projects/context.md"),
                new NeuronRef("table", "neuron://local/postgres/main#query-result"),
                new NeuronRef("view", "neuron://local/apps/gallery/view"),
            ])
            { Gap = 10 },

            _ => new RecordCardNode(
                selected.Category,
                selected.Title,
                [
                    new RecordFieldNode("Contract", selected.NeuronContract),
                    new RecordFieldNode("Neuron URI", selected.SourceNeuronUri),
                    new RecordFieldNode("Signals", string.Join(", ", selected.Signals)),
                ],
                Subtitle: selected.Summary),
        };
    }

    private string BuildDynamicCodeSnippet(GalleryCatalogEntry selected)
    {
        if (State.CodeTab == "flutter")
        {
            var primary = State.KnobStyle == "primary" ? "true" : "false";
            var danger = State.KnobStyle == "danger" ? "true" : "false";
            var small = State.KnobSmall ? "true" : "false";
            return $"// NeuronView -> _ImmutableView ({State.StylePreset} · {State.BarLayout})\n"
                + $"// Surface: {State.KnobSurface} · Tone: {State.KnobTone} · State: {State.KnobState}\n"
                + $"IcAcrylicBox(\n"
                + $"  padding: const EdgeInsets.all({(State.KnobSmall ? 14 : 20)}),\n"
                + $"  child: Column(\n"
                + $"    children: [\n"
                + $"      TagPill(label: '{selected.Signals.FirstOrDefault() ?? "gallery.select"}', tone: TagTone.{State.KnobTone}),\n"
                + $"      Btn(label: '{selected.Title}', primary: {primary}, danger: {danger}, small: {small}),\n"
                + $"    ],\n"
                + $"  ),\n"
                + $")";
        }

        if (State.CodeTab == "contract")
        {
            return $"Contract: {selected.NeuronContract}\n"
                + $"URI:      {selected.SourceNeuronUri}\n"
                + $"Signals:  [Signal] {string.Join(", ", selected.Signals)} (fired {State.SignalPulseCount} time(s))\n"
                + $"Identity: {State.StylePreset} ({State.BarLayout})\n"
                + $"Knobs:    style={State.KnobStyle}, tone={State.KnobTone}, small={State.KnobSmall}, state={State.KnobState}, surface={State.KnobSurface}";
        }

        return $"// {selected.NeuronContract} · [{string.Join(", ", selected.Signals)}]\n"
            + $"// Knobs: Style={State.KnobStyle} · Tone={State.KnobTone} · Size={(State.KnobSmall ? "Small(30px)" : "Standard(36px)")} · Surface={State.KnobSurface}\n"
            + selected.CSharpSample;
    }

    private static Button KnobButton(string label, string selectArg, bool active) => new(
        label,
        new ButtonAction("gallery", "select", [selectArg]),
        Tone: active ? ButtonTone.Accent : ButtonTone.Neutral)
    {
        Style = active ? "primary" : "secondary",
        Small = true,
    };

    private static BadgeTone MapKnobBadgeTone(string tone) => tone.ToLowerInvariant() switch
    {
        "done" or "ok" or "success" => BadgeTone.Done,
        "working" or "warn" or "warning" => BadgeTone.Working,
        "error" or "danger" => BadgeTone.Error,
        "neutral" => BadgeTone.Neutral,
        _ => BadgeTone.Accent,
    };

    private static string NormalizeCategory(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "design & identity" or "design" => "design",
        "basic input" or "input" or "controls" => "controls",
        "layout & app shell" or "layout" => "layout",
        "data & 13 lfp chat cards" or "data" or "lfp" or "chat-lfp" => "chat-lfp",
        "3d brain & neurons" or "3d" or "brain" or "3d-brain" => "3d-brain",
        _ => "all",
    };

    private static string CategoryDisplayName(string category) => category switch
    {
        "design" => "Design & Identity",
        "controls" => "Basic Input",
        "layout" => "Layout & App Shell",
        "chat-lfp" => "Data & 13 LFP Chat Cards",
        "3d-brain" => "3D Brain & Neurons",
        _ => "All Controls",
    };

    private static string CategoryShortBadge(string category) => category switch
    {
        "design" => "Design",
        "controls" => "Input",
        "layout" => "Layout",
        "chat-lfp" => "LFP",
        "3d-brain" => "3D",
        _ => "UI",
    };

    private static string DefaultBarLayoutFor(string preset, string fallback) => preset switch
    {
        "fluent-acrylic" or "fluent-warm-mica" => "floating-glass",
        "spatial-glass" => "compact-pill",
        "cyber-synaptic" or "nordic-tactical" => "split-top-dock",
        "warm-paper" or "linear-carbon" => "classic-bottom",
        _ => fallback,
    };

    [DigitalBrain.Scenario("Gallery filters controls, inspects NodeContract and neuron:// URI, and switches UI identity")]
    public async Task VerifyGalleryCatalogAndIdentitySwitchScenario()
    {
        await Open(new GalleryOpen());
        Filter(new GalleryFilter("chat-lfp", "Table"));
        if (State.SelectedId != "table-card")
        {
            throw new InvalidOperationException($"Expected 'table-card' to be selected after filtering, got '{State.SelectedId}'.");
        }

        await SwitchIdentity(new GallerySwitchIdentity("cyber-synaptic", "split-top-dock"));
        if (State.StylePreset != "cyber-synaptic" || State.BarLayout != "split-top-dock")
        {
            throw new InvalidOperationException("Expected GalleryApp to switch StylePreset and BarLayout.");
        }
    }

    private GalleryCatalogEntry[] FilteredEntries(string category, string search)
        => State.Entries
            .Where(e => category == "all" || string.Equals(e.Category, category, StringComparison.OrdinalIgnoreCase))
            .Where(e => string.IsNullOrWhiteSpace(search)
                || e.Id.Contains(search, StringComparison.OrdinalIgnoreCase)
                || e.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                || e.Summary.Contains(search, StringComparison.OrdinalIgnoreCase)
                || e.NeuronContract.Contains(search, StringComparison.OrdinalIgnoreCase)
                || e.Signals.Any(sig => sig.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

    private IUserSettings? TryGetSettings()
    {
        try
        {
            return Get<IUserSettings>("settings");
        }
        catch (InvalidOperationException error)
        {
            Swallow(error, "UserSettingsNeuron is optional when running GalleryApp in isolation.");
            return null;
        }
    }
}
