using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Gallery;

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

[GenerateSerializer, Alias("os.gallery.library")]
public sealed record GalleryLibraryState(
    [property: Id(0)] string Category,
    [property: Id(1)] string Search,
    [property: Id(2)] string SelectedId,
    [property: Id(3)] int Count)
{
    public GalleryLibraryState() : this("all", "", "approval-card", 28) { }
}

[DigitalBrain.Contracts.NeuronAlias("os.gallery.library"), DigitalBrain.Contracts.DefaultNeuronType("os.gallery.library")]
public interface IGalleryLibrary
{
    [DigitalBrain.Contracts.ReadOnlyCall]
    Task<GalleryLibraryState> Read();

    Task<GalleryLibraryState> Filter(string category, string search);

    Task<GalleryLibraryState> Select(string selectedId);
}

[DigitalBrain.Neurons.Neuron("os.gallery.library")]
public sealed class GalleryLibrary : DigitalBrain.Neurons.Neuron<GalleryLibraryState>, IGalleryLibrary
{
    public Task<GalleryLibraryState> Read() => Task.FromResult(State);

    public async Task<GalleryLibraryState> Filter(string category, string search)
    {
        var normalized = NormalizeCategory(category);
        var matches = FilteredEntries(DefaultCatalog, normalized, search);
        var selected = matches.Any(e => e.Id == State.SelectedId)
            ? State.SelectedId
            : matches.FirstOrDefault()?.Id ?? State.SelectedId;
        var next = new GalleryLibraryState(normalized, search, selected, matches.Length);
        await Save(next, new GalleryFilter(normalized, search));
        return next;
    }

    public async Task<GalleryLibraryState> Select(string selectedId)
    {
        var next = State with { SelectedId = selectedId };
        await Save(next, new GallerySelect(selectedId));
        return next;
    }

    public static GalleryCatalogEntry[] FilteredEntries(GalleryCatalogEntry[] entries, string category, string search)
        => entries
            .Where(e => category == "all" || string.Equals(e.Category, category, StringComparison.OrdinalIgnoreCase))
            .Where(e => string.IsNullOrWhiteSpace(search)
                || e.Id.Contains(search, StringComparison.OrdinalIgnoreCase)
                || e.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                || e.Summary.Contains(search, StringComparison.OrdinalIgnoreCase)
                || e.NeuronContract.Contains(search, StringComparison.OrdinalIgnoreCase)
                || e.Signals.Any(sig => sig.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

    public static string NormalizeCategory(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "design & identity" or "design" => "design",
        "basic input" or "input" or "controls" => "controls",
        "layout & app shell" or "layout" => "layout",
        "data & 13 lfp chat cards" or "data" or "lfp" or "chat-lfp" => "chat-lfp",
        "3d brain & neurons" or "3d" or "brain" or "3d-brain" => "3d-brain",
        _ => "all",
    };

    public static string DefaultBarLayoutFor(string preset, string fallback) => preset switch
    {
        "fluent-acrylic" or "fluent-warm-mica" => "floating-glass",
        "spatial-glass" => "compact-pill",
        "cyber-synaptic" or "nordic-tactical" => "split-top-dock",
        "warm-paper" or "linear-carbon" => "classic-bottom",
        _ => fallback,
    };

    public static readonly GalleryCatalogEntry[] DefaultCatalog =
    [
        // 1. Design & Identity ("design")
        new("themes", "design", "7 UI Identities & Glass Materiality", "Fluent Acrylic, Spatial Glass, Cyber Synaptic, Warm Paper, Warm Mica, Linear Carbon & Nordic Tactical.", "IntoChat.OS.Identity.IUserSettings@1", ["gallery.switch-identity", "settings.save-preferences"], "neuron://local/ui.gallery/themes", "await settings.SetUiIdentity(\"fluent-acrylic\", \"floating-glass\");") { Icon = "palette" },
        new("typography", "design", "Typography (Inter + InterDisplay + tnum)", "Inter and InterDisplay scale with strict tabular numerals (tnum) for live telemetry.", "IntoChat.OS.Modules.Flutter.Views.IView@1", ["FlutterViewChanged"], "neuron://local/ui.gallery/typography", "new Heading(\"InterDisplay 32 · 1,492,048 tok\", Level: 1, Subtitle: \"Tabular numerals keep live telemetry aligned.\");") { Icon = "type" },
        new("iconography", "design", "Iconography & Squircle IcAppIcon Builder", "Two-stop gradient squircle app badges with 1px specular border and custom/Lucide vector glyphs.", "DigitalBrain.IScreen@1", ["AppRegisteredSignal"], "neuron://local/ui.gallery/iconography", "[Neuron(\"app.gallery\")]") { Icon = "sparkles" },
        new("acrylic-surface", "design", "AcrylicSurface & Specular Border", "Translucent dark obsidian glass with 24px backdrop blur and 1px top-lit specular border.", "IntoChat.UI.Surface@1", ["settings.save-preferences"], "neuron://local/ui.gallery/acrylic-surface", "Ui.AppShell(\"shell\", sidebar: Ui.NavItem(\"nav\", \"Overview\", \"layout-grid\", \"open\", selected: true), body: Ui.StatCard(\"kpi\", \"Blur\", \"28px\", \"+100%\", BadgeTone.Accent));") { Icon = "layers" },

        // 2. Basic Input ("controls")
        new("buttons", "controls", "Btn (Styles, Sizes & Icons)", "Primary, Secondary, Ghost, and Danger buttons in Standard (36px) and Small (30px) sizes.", "IntoChat.OS.Modules.Flutter.Views.IView@1", ["FlutterViewChanged"], "neuron://local/ui.gallery/buttons", "new Button(\"execute\", \"Execute Action\", Command: new Command(\".\", \"select\", JsonSerializer.SerializeToElement(\"pulse\")), Tone: ButtonTone.Accent);") { Icon = "mouse-pointer-click" },
        new("inputs", "controls", "Field & TextField", "Desktop-first EditableText fields with neuron:// address validation and instant submit.", "IntoChat.OS.Modules.Flutter.Fields.IField@1", ["FieldChanged"], "neuron://local/ui.gallery/inputs", "new Field(\"address\", \"address\", FieldKind.Text, \"Enter neuron:// address…\", new Emit(\"gallery\", \"select\", JsonSerializer.SerializeToElement(\"pulse\")));") { Icon = "text-cursor-input" },
        new("switch", "controls", "IcSwitch (with Lock State)", "Instant-save toggle switch with policy lock indicator and keyboard focus ring.", "IntoChat.OS.Modules.Flutter.Switch.ISwitch@1", ["SwitchToggledSignal"], "neuron://local/ui.gallery/switch", "new Switch(\"stream\", \"Live [Signal] stream active\", IsOn: true, Emit: new Emit(\"gallery\", \"select\", JsonSerializer.SerializeToElement(\"toggle:switch\")), Locked: false);") { Icon = "toggle-right" },
        new("select", "controls", "Select & Dropdown", "Single-choice segmented and dropdown selectors backed by DigitalBrain.UI.Select.", "DigitalBrain.UI.Select@1", ["SelectionChangedSignal"], "neuron://local/ui.gallery/select", "new Select(\"Active Reasoning Model\", \"claude-sonnet-4-5\", [new SelectOption(\"claude\", \"claude-sonnet-4-5\", \"Claude Sonnet 4.5\")]);") { Icon = "list-filter" },
        new("tag-pill", "controls", "TagPill & Status Badges", "Semantic status pills (Neutral, Accent, Done, Working, Error) paired with vector glyphs.", "IntoChat.OS.Modules.Flutter.Badge.IBadge@1", ["StatusToneChanged"], "neuron://local/ui.gallery/tag-pill", "new Row([new TagPill(\"NEUTRAL\", \"neutral\", \"sparkles\"), new TagPill(\"ACCENT\", \"accent\", \"zap\"), new TagPill(\"DONE\", \"done\", \"check\")]);") { Icon = "tag" },
        new("progress-infobar", "controls", "Progress & InfoBar", "Determinate progress bars and contextual status banners with action buttons.", "IntoChat.OS.Modules.Flutter.Views.IView@1", ["FlutterViewChanged"], "neuron://local/ui.gallery/progress-infobar", "new InfoBar(\"Orleans grain pool synced\", \"14/14 [Scenario] gates passed without warnings.\", Tone: \"info\");") { Icon = "loader" },
        new("files-neuron-uri", "controls", "Distributed neuron:// File Addressing", "Hybrid cloud + local file explorer exposing canonical neuron://{brain}/{connection}.files/{path} URIs.", "IntoChat.UI.Collection@1", ["files.location", "files.selected"], "neuron://owner/drive.files/projects/context.md", "var uri = filesApp.NeuronUriOf(selectedItem);") { Icon = "folder" },

        // 3. Layout & App Shell ("layout")
        new("app-shell", "layout", "Ui.AppShell & Ui.StatCard Builders", "Ergonomic 30-line C# desktop layout builder with sidebar, toolbar, body, and split inspector.", "DigitalBrain.UI.AppShellNode@1", ["gallery.select"], "neuron://local/ui.gallery/app-shell", "Ui.AppShell(\"studio\", sidebar: Ui.NavItem(\"n1\", \"Studio\", \"sparkles\", \"open\", selected: true), body: Ui.StatCard(\"s1\", \"Throughput\", \"142 sig/m\", \"+18%\", BadgeTone.Success));") { Icon = "panel-left" },
        new("tabs", "layout", "Tabs & Segmented Views", "Multi-pane tab strips switching live DigitalBrain.UI view subtrees.", "DigitalBrain.UI.Tabs@1", ["ActiveTabChanged"], "neuron://local/ui.gallery/tabs", "new Tabs([new TabItem(\"overview\", \"overview\", \"Overview\", AppSignals.Emit(\"gallery\", \"select\", \"shell:overview\"))], \"overview\");") { Icon = "folder-kanban" },
        new("surfaces", "layout", "IcAcrylicBox Surfaces", "Acrylic, Elevated, and Recessed containers with adaptive border hairlines.", "DigitalBrain.UI.Surface@1", ["SurfaceMutatedSignal"], "neuron://local/ui.gallery/surfaces", "new Surface([new Text(\"Adaptive glass surface\")], Kind: SurfaceKind.Acrylic, Title: \"Acrylic Surface\", Subtitle: \"1px specular border\");") { Icon = "square-stack" },
        new("stat-card", "layout", "StatCard (Ui.StatCard)", "Tabular KPI metric cards with semantic delta TagPill and vector icon.", "DigitalBrain.UI.StatCardNode@1", ["MetricRefreshedSignal"], "neuron://local/ui.gallery/stat-card", "Ui.StatCard(\"kpi-tokens\", \"Compute Throughput\", \"842,100 tok\", \"+14.2%\", BadgeTone.Done, \"zap\");") { Icon = "trending-up" },

        // 4. Data & 13 LFP Chat Cards ("chat-lfp")
        new("table-card", "chat-lfp", "TableCard (LFP)", "Interactive sortable data table rendered from workspace files or live neuron state.", "IntoChat.UI.TableCard@1", ["ino.query-workspace-file", "ino.analyze-workspace-data"], "neuron://local/ui.gallery/table-card", "await DataAnalyticsEngine.AnalyzeWorkspaceAsync(vfs, \"metrics.csv\");") { Icon = "table" },
        new("chart-card", "chat-lfp", "ChartCard (LFP)", "Inline column and line chart card with comparison series and metric summary.", "IntoChat.UI.ChartCard@1", ["ino.send"], "neuron://local/ui.gallery/chart-card", "new Entry(\"chart-q4\", \"Throughput per month, 2026\", \"neuron://local/analytics/q4#chart\");") { Icon = "chart-column" },
        new("approval-card", "chat-lfp", "ApprovalCard (LFP)", "Inline human-in-the-loop confirmation card rendered inside Ino chat turns and Smart Desktop.", "IntoChat.UI.ApprovalCard@1", ["confirmations.Resolve"], "neuron://local/os.confirmations/inbox#approval-card", "await ApproveCard(new InoApproveCard(\"card-1\", Approved: true));") { Icon = "check-check" },
        new("choice-card", "chat-lfp", "ChoiceCard (LFP)", "Numbered disambiguation options rendered inline in Ino sessions.", "IntoChat.UI.ChoiceCard@1", ["ChoiceSelectedSignal"], "neuron://local/apps/ino#choice-card", "new ChoiceCardNode(\"choice-1\", \"Which workspace should receive the deployment digest?\", [new ChoiceOptionNode(\"Primary Workspace\", \"neuron://local/circles/primary\")]);") { Icon = "list-checks" },
        new("draft-card", "chat-lfp", "DraftCard (LFP)", "Inline editable email/Slack/SMS draft with Send, Schedule, and Discard actions.", "IntoChat.UI.DraftCard@1", ["DraftDispatchedSignal"], "neuron://local/apps/ino#draft-card", "new DraftCardNode(\"draft-1\", \"email\", \"user@intochat.os\", \"team@intochat.os\", \"Confirmed: Release schedule updated.\", Subject: \"Release Schedule\");") { Icon = "mail" },
        new("change-preview-card", "chat-lfp", "ChangePreviewCard (LFP)", "Field-by-field mutation preview with inline before/after values and confirmation.", "IntoChat.UI.ChangePreviewCard@1", ["ChangeAppliedSignal"], "neuron://local/os.confirmations/changes#preview", "new ApprovalCardNode(\"change-1\", \"Update Budget · Compute Cap\", \"Compute · 2 field changes\", [new ApprovalRowNode(\"Monthly Cap\", \"$50.00\", \"$75.00\")], SystemName: \"Compute\");") { Icon = "git-compare" },
        new("app-install-card", "chat-lfp", "AppInstallCard (LFP)", "Verified Smart App manifest card with capability review and 1-click install.", "IntoChat.UI.AppInstallCard@1", ["AppInstallReviewedSignal"], "neuron://local/apps/installer#unit-sync", "new RecordCardNode(\"Smart App Manifest\", \"GitHub Issue Triager (v1.0.0)\", [new RecordFieldNode(\"Publisher\", \"IntoChat OS (Verified)\"), new RecordFieldNode(\"Capabilities\", \"Http (read), Storage (write)\")], LinkLabel: \"Review & Install\");") { Icon = "package-plus" },
        new("record-card", "chat-lfp", "RecordCard (LFP)", "Two-column entity snapshot card with status and deep-link navigation.", "IntoChat.UI.RecordCard@1", ["RecordLoadedSignal"], "neuron://local/units/watch.web#snapshot", "new RecordCardNode(\"Synapse Unit\", \"Web Watch Monitor\", [new RecordFieldNode(\"Schedule\", \"Every 15m\"), new RecordFieldNode(\"Status\", \"Healthy\")], Subtitle: \"neuron://local/units/watch.web\");") { Icon = "contact" },
        new("secret-card", "chat-lfp", "SecretCard (LFP)", "Zero-leakage credential request card routing secrets directly to Vault.", "IntoChat.UI.SecretCard@1", ["SecretStoredSignal"], "neuron://local/vault/secrets#api-token", "new SecretCardNode(\"GitHub API Token\", \"Required to read repository issues.\", SavedIn: \"Secure Vault · neuron://local/vault/secrets\");") { Icon = "key-round" },
        new("job-card", "chat-lfp", "JobCard (LFP)", "Multi-step background workflow progress card with live step status.", "IntoChat.UI.JobCard@1", ["JobStepAdvancedSignal"], "neuron://local/runtime/jobs#bdd-gate", "new JobCardNode(\"job-1\", \"Verifying BDD [Scenario] Gate & Deploying Smart App\", \"running\", 0.75, [new JobStepNode(\"Compile C# App<TState> grain\", \"done\", \"118ms\"), new JobStepNode(\"Execute [Scenario] BDD assertions\", \"done\", \"14/14 green\")]);") { Icon = "workflow" },
        new("remember-card", "chat-lfp", "RememberCard (LFP)", "Episodic memory confirmation card for saving facts to DigitalBrain.Memory.", "DigitalBrain.Memory.IEpisodicMemory@1", ["MemoryCommittedSignal"], "neuron://local/memory/episodes#pref-format", "await memory.RememberFact(\"preference\", \"Always format telemetry latency in milliseconds with tabular figures.\", importance: 8);") { Icon = "brain" },
        new("file-cards", "chat-lfp", "FileCards (FileCard, FileChip & SaveToPicker) (LFP)", "Artifact write cards, hover path chips, and canonical neuron:// file references.", "IntoChat.OS.Storage.IFilesWorkspace@1", ["FileSavedSignal"], "neuron://local/storage.files/work/notes.md", "new RecordCardNode(\"Files Vault\", \"notes.md\", [new RecordFieldNode(\"Path\", \"neuron://local/storage.files/work/notes.md\"), new RecordFieldNode(\"Size\", \"4 KB · Workspace\")], Subtitle: \"Saved structured table snapshot.\", LinkLabel: \"Open in Files\");") { Icon = "file-check" },
        new("error-line", "chat-lfp", "ErrorLine (LFP)", "Single-line actionable recovery bar for timeouts, rate limits, and auth grants.", "IntoChat.OS.Assistant.ISessionDiagnostics@1", ["RecoveryTriggeredSignal"], "neuron://local/apps/ino#diagnostics", "new InfoBar(\"Upstream endpoint timed out\", \"Nothing was changed yet. Click Retry to reconnect with the capability broker.\", Tone: \"danger\");") { Icon = "triangle-alert" },

        // 5. 3D Brain & Neurons ("3d-brain")
        new("brain-scene-3d", "3d-brain", "BrainScene3D Orbital Graph", "Interactive 3D orbital projection of active neurons, modules, and traveling [Signal] photon pulses.", "IntoChat.UI.BrainScene@1", ["creator.inspect", "creator.prompt"], "neuron://local/creator/brain-scene#orbital", "await Inspect(new CreatorInspect(\"OperatingSystem\"));") { Icon = "brain-circuit" },
        new("neuron-paths", "3d-brain", "Distributed NeuronId Addressing", "Canonical neuron:// URIs across Files, UI controls, and cross-brain Group grants.", "DigitalBrain.NeuronId@1", ["SynapseRoutedSignal"], "neuron://local/storage.files/work/context.md", "new NeuronRef(\"files\", \"neuron://local/storage.files/work/context.md\");") { Icon = "network" },

        // 6. WinUI 3 Wave 3 Neurons
        new("stepper", "layout", "Stepper (IStepper)", "Workflow stage indicator neuron with interactive step selection and Advance.", "IntoChat.OS.Modules.UI.Stepper.IStepper@1", ["ui.stepper-changed"], "neuron://local/ui.stepper/demo", "await stepper.Configure([new StepperStep(\"plan\", \"Plan\", \"done\"), new StepperStep(\"build\", \"Build\", \"active\")]);") { Icon = "milestone" },
        new("property-grid", "controls", "PropertyGrid (IPropertyGrid)", "Live JSON and neuron state tree inspector with filter and inline value editing.", "IntoChat.OS.Modules.UI.PropertyGrid.IPropertyGrid@1", ["ui.property-grid-changed"], "neuron://local/ui.property-grid/demo", "await grid.LoadJson(\"Neuron State\", stateJson);") { Icon = "braces" },
        new("kanban-board", "layout", "KanbanBoard (IKanbanBoard)", "Multi-column Kanban board neuron with WIP limits and movable cards.", "IntoChat.OS.Modules.UI.KanbanBoard.IKanbanBoard@1", ["ui.kanban-changed"], "neuron://local/ui.kanban/demo", "await kanban.MoveCard(\"c1\", \"in-progress\");") { Icon = "kanban" },
        new("file-picker", "controls", "FilePicker (IFilePicker)", "Files VFS path, extension filter, and multi-select file picker neuron.", "IntoChat.OS.Modules.UI.FilePicker.IFilePicker@1", ["ui.file-picker-changed"], "neuron://local/ui.file-picker/demo", "await picker.Select(\"/workspace/GalleryApp.cs\");") { Icon = "folder-open" },
    ];
}

public sealed record GalleryIconCatalogEntry(string Slug, string Category, string Badge);

public static class GalleryIconCatalog
{
    public static readonly string[] Categories =
    [
        "All",
        "OS",
        "Code/DDD",
        "AI/Neurons",
        "Files",
        "Media",
        "Navigation",
        "Status",
        "Devices",
    ];

    public static readonly GalleryIconCatalogEntry[] All =
    [
        // OS
        new("app-window", "OS", "OS"),
        new("settings", "OS", "OS"),
        new("sliders-horizontal", "OS", "OS"),
        new("sliders-vertical", "OS", "OS"),
        new("palette", "OS", "OS"),
        new("paintbrush", "OS", "OS"),
        new("sun", "OS", "OS"),
        new("moon", "OS", "OS"),
        new("command", "OS", "OS"),
        new("power", "OS", "OS"),
        new("layers", "OS", "OS"),
        new("square-stack", "OS", "OS"),
        new("layout-grid", "OS", "OS"),
        new("columns-3", "OS", "OS"),
        new("copy", "OS", "OS"),
        new("key-round", "OS", "OS"),
        new("lock", "OS", "OS"),
        new("user", "OS", "OS"),
        new("users", "OS", "OS"),
        // Code/DDD
        new("code", "Code/DDD", "DDD"),
        new("code-xml", "Code/DDD", "DDD"),
        new("braces", "Code/DDD", "DDD"),
        new("brackets", "Code/DDD", "DDD"),
        new("terminal", "Code/DDD", "DDD"),
        new("bug", "Code/DDD", "DDD"),
        new("hammer", "Code/DDD", "DDD"),
        new("wrench", "Code/DDD", "DDD"),
        new("git-branch", "Code/DDD", "DDD"),
        new("git-commit", "Code/DDD", "DDD"),
        new("git-compare", "Code/DDD", "DDD"),
        new("git-merge", "Code/DDD", "DDD"),
        new("git-pull-request", "Code/DDD", "DDD"),
        new("blocks", "Code/DDD", "DDD"),
        new("component", "Code/DDD", "DDD"),
        new("puzzle", "Code/DDD", "DDD"),
        new("binary", "Code/DDD", "DDD"),
        new("regex", "Code/DDD", "DDD"),
        new("variable", "Code/DDD", "DDD"),
        new("database", "Code/DDD", "DDD"),
        new("table", "Code/DDD", "DDD"),
        new("kanban", "Code/DDD", "DDD"),
        new("square-kanban", "Code/DDD", "DDD"),
        new("folder-kanban", "Code/DDD", "DDD"),
        // AI/Neurons
        new("sparkles", "AI/Neurons", "AI"),
        new("wand-sparkles", "AI/Neurons", "AI"),
        new("brain", "AI/Neurons", "AI"),
        new("brain-circuit", "AI/Neurons", "AI"),
        new("brain-cog", "AI/Neurons", "AI"),
        new("bot", "AI/Neurons", "AI"),
        new("bot-message-square", "AI/Neurons", "AI"),
        new("network", "AI/Neurons", "AI"),
        new("workflow", "AI/Neurons", "AI"),
        new("atom", "AI/Neurons", "AI"),
        new("orbit", "AI/Neurons", "AI"),
        new("lightbulb", "AI/Neurons", "AI"),
        new("radar", "AI/Neurons", "AI"),
        new("zap", "AI/Neurons", "AI"),
        new("activity", "AI/Neurons", "AI"),
        new("gauge", "AI/Neurons", "AI"),
        // Files
        new("file", "Files", "FILE"),
        new("file-text", "Files", "FILE"),
        new("file-code", "Files", "FILE"),
        new("file-json", "Files", "FILE"),
        new("file-diff", "Files", "FILE"),
        new("file-search", "Files", "FILE"),
        new("file-check", "Files", "FILE"),
        new("file-lock", "Files", "FILE"),
        new("file-archive", "Files", "FILE"),
        new("folder", "Files", "FILE"),
        new("folder-open", "Files", "FILE"),
        new("folder-git-2", "Files", "FILE"),
        new("folder-tree", "Files", "FILE"),
        new("cloud", "Files", "FILE"),
        new("cloud-upload", "Files", "FILE"),
        new("cloud-download", "Files", "FILE"),
        // Media
        new("image", "Media", "MEDIA"),
        new("camera", "Media", "MEDIA"),
        new("aperture", "Media", "MEDIA"),
        new("clapperboard", "Media", "MEDIA"),
        new("play", "Media", "MEDIA"),
        new("pause", "Media", "MEDIA"),
        new("circle-play", "Media", "MEDIA"),
        new("music", "Media", "MEDIA"),
        new("headphones", "Media", "MEDIA"),
        new("mic", "Media", "MEDIA"),
        new("video", "Media", "MEDIA"),
        new("volume-2", "Media", "MEDIA"),
        // Navigation
        new("house", "Navigation", "NAV"),
        new("search", "Navigation", "NAV"),
        new("compass", "Navigation", "NAV"),
        new("map", "Navigation", "NAV"),
        new("map-pin", "Navigation", "NAV"),
        new("navigation", "Navigation", "NAV"),
        new("milestone", "Navigation", "NAV"),
        new("signpost", "Navigation", "NAV"),
        new("panel-left", "Navigation", "NAV"),
        new("panel-right", "Navigation", "NAV"),
        new("panel-top", "Navigation", "NAV"),
        new("bookmark", "Navigation", "NAV"),
        new("external-link", "Navigation", "NAV"),
        new("funnel", "Navigation", "NAV"),
        // Status
        new("check", "Status", "STAT"),
        new("circle-check", "Status", "STAT"),
        new("badge-check", "Status", "STAT"),
        new("badge-info", "Status", "STAT"),
        new("badge-alert", "Status", "STAT"),
        new("bell", "Status", "STAT"),
        new("triangle-alert", "Status", "STAT"),
        new("shield-check", "Status", "STAT"),
        new("shield-alert", "Status", "STAT"),
        new("loader-circle", "Status", "STAT"),
        new("flame", "Status", "STAT"),
        new("rocket", "Status", "STAT"),
        new("chart-column", "Status", "STAT"),
        new("trending-up", "Status", "STAT"),
        // Devices
        new("monitor", "Devices", "DEV"),
        new("laptop", "Devices", "DEV"),
        new("smartphone", "Devices", "DEV"),
        new("tablet", "Devices", "DEV"),
        new("cpu", "Devices", "DEV"),
        new("microchip", "Devices", "DEV"),
        new("hard-drive", "Devices", "DEV"),
        new("server", "Devices", "DEV"),
        new("router", "Devices", "DEV"),
        new("wifi", "Devices", "DEV"),
        new("bluetooth", "Devices", "DEV"),
        new("usb", "Devices", "DEV"),
        new("keyboard", "Devices", "DEV"),
        new("mouse", "Devices", "DEV"),
    ];

    public static GalleryIconCatalogEntry[] ForCategory(string? category)
    {
        if (string.IsNullOrWhiteSpace(category) || string.Equals(category, "All", StringComparison.OrdinalIgnoreCase))
        {
            return All;
        }
        return All.Where(e => string.Equals(e.Category, category, StringComparison.OrdinalIgnoreCase)).ToArray();
    }
}
