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
    [DigitalBrain.ReadOnlyCall]
    Task<GalleryLibraryState> Read();

    Task<GalleryLibraryState> Filter(string category, string search);

    Task<GalleryLibraryState> Select(string selectedId);
}

[DigitalBrain.Neuron("os.gallery.library")]
public sealed class GalleryLibrary : DigitalBrain.Neuron<GalleryLibraryState>, IGalleryLibrary
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

    public static string CategoryDisplayName(string category) => category switch
    {
        "design" => "Design & Identity",
        "controls" => "Basic Input",
        "layout" => "Layout & App Shell",
        "chat-lfp" => "Data & 13 LFP Chat Cards",
        "3d-brain" => "3D Brain & Neurons",
        _ => "All Controls",
    };

    public static string CategoryShortBadge(string category) => category switch
    {
        "design" => "Design",
        "controls" => "Input",
        "layout" => "Layout",
        "chat-lfp" => "LFP",
        "3d-brain" => "3D",
        _ => "UI",
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
            "new Button(\"execute\", \"Execute Action\", Emit: new Emit(\"gallery\", \"select\", JsonSerializer.SerializeToElement(\"pulse\")), Tone: ButtonTone.Accent) { Icon = \"sparkles\", Small = false };")
        { Icon = "mouse-pointer-click" },
        new(
            "inputs",
            "controls",
            "Field & TextField",
            "Desktop-first EditableText fields with neuron:// address validation and instant submit.",
            "IntoChat.OS.Modules.Flutter.TextField.ITextField@1",
            ["TextFieldChanged"],
            "neuron://local/ui.gallery/inputs",
            "new Field(\"address\", \"address\", FieldKind.Text, \"Enter neuron:// address…\", new Emit(\"gallery\", \"select\", JsonSerializer.SerializeToElement(\"pulse\")));")
        { Icon = "text-cursor-input" },
        new(
            "switch",
            "controls",
            "IcSwitch (with Lock State)",
            "Instant-save toggle switch with policy lock indicator and keyboard focus ring.",
            "IntoChat.OS.Modules.Flutter.Switch.ISwitch@1",
            ["SwitchToggledSignal"],
            "neuron://local/ui.gallery/switch",
            "new Switch(\"stream\", \"Live [Signal] stream active\", IsOn: true, Emit: new Emit(\"gallery\", \"select\", JsonSerializer.SerializeToElement(\"toggle:switch\")), Locked: false);")
        { Icon = "toggle-right" },
        new(
            "select",
            "controls",
            "Select & Dropdown",
            "Single-choice segmented and dropdown selectors backed by DigitalBrain.UI.Select.",
            "IntoChat.OS.Modules.Flutter.Select.ISelect@1",
            ["SelectionChangedSignal"],
            "neuron://local/ui.gallery/select",
            "new Select(\"Active Reasoning Model\", \"claude-sonnet-4-5\", [new SelectOption(\"claude\", \"claude-sonnet-4-5\", \"Claude Sonnet 4.5\"), new SelectOption(\"gpt\", \"gpt-5-thinking\", \"GPT-5 Thinking\")]);")
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
            "new Tabs(\"overview\", [new TabItem(\"overview\", \"overview\", \"Overview\", new Emit(\"gallery\", \"select\", JsonSerializer.SerializeToElement(\"shell:overview\"))), new TabItem(\"signals\", \"signals\", \"Signals\", new Emit(\"gallery\", \"select\", JsonSerializer.SerializeToElement(\"shell:signals\")))]);")
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
            ["confirmations.Resolve"],
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
            "new InfoBar(\"Connection to Salesforce timed out\", \"Nothing was changed yet. Click Retry to reconnect with the capability broker.\", Tone: \"danger\", Action: new Button(\"retry\", \"Retry\", Emit: new Emit(\"gallery\", \"select\", JsonSerializer.SerializeToElement(\"pulse\")), Tone: ButtonTone.Accent) { Small = true });")
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
