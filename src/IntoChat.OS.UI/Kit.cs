using System.Linq.Expressions;
using DigitalBrain.Rules;
using DigitalBrain.UI;
using IntoChat.OS.UI.Dispatch;

namespace IntoChat.OS.UI;

/// <summary>
/// Canonical 4px spacing scale tokens from <c>DESIGN.md</c> §4.1.
/// </summary>
public static class Space
{
    public const double Xxs = 2;
    public const double Xs = 4;
    public const double Sm = 8;
    public const double Md = 12;
    public const double Lg = 16;
    public const double Xl = 24;
    public const double Xxl = 32;
    public const double Xxxl = 48;
}

/// <summary>
/// Semantic status vocabulary from <c>DESIGN.md</c> §2.5 ("Status Is Never Colour Alone").
/// </summary>
public enum StatusKind
{
    Done,
    Working,
    Error,
    Idle,
    NeedsYou,
}

/// <summary>
/// Common Lucide icon slugs used across IntoChat OS surfaces.
/// </summary>
public static class Icons
{
    public const string Check = "check";
    public const string LoaderCircle = "loader-circle";
    public const string TriangleAlert = "triangle-alert";
    public const string Hand = "hand";
    public const string Plus = "plus";
    public const string Search = "search";
    public const string Sliders = "sliders-horizontal";
    public const string Sparkles = "sparkles";
    public const string Landmark = "landmark";
    public const string PanelLeft = "panel-left";
    public const string RefreshCw = "refresh-cw";
    public const string Trash2 = "trash-2";
    public const string KeyRound = "key-round";
    public const string Folder = "folder";
    public const string Play = "play";
    public const string FileText = "file-text";
    public const string MessageSquare = "message-square";
    public const string LayoutGrid = "layout-grid";
}

/// <summary>
/// Base fluent builder node in <see cref="Kit"/> that converts implicitly to a wire-compatible <see cref="DigitalBrain.UI.Node"/>.
/// </summary>
public abstract class KitNode
{
    /// <summary>Materializes this fluent builder into an immutable <see cref="DigitalBrain.UI.Node"/>.</summary>
    public abstract Node ToNode();

    /// <summary>Implicitly converts any <see cref="KitNode"/> builder into a <see cref="DigitalBrain.UI.Node"/>.</summary>
    public static implicit operator Node(KitNode builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.ToNode();
    }
}

/// <summary>
/// Fluent C# Reactor static UI DSL (<c>using static IntoChat.OS.UI.Kit;</c>) emitting 100% wire-compatible
/// <see cref="DigitalBrain.UI.Node"/> trees aligned with <c>DESIGN.md</c>.
/// </summary>
public static class Kit
{
    // ── Layout ───────────────────────────────────────────────────────────────

    /// <summary>Vertical stack (<see cref="Column"/>). Supports both <c>VStack(a, b)</c> and <c>VStack(gap: Space.Sm)[ a, b ]</c>.</summary>
    public static StackBuilder VStack(double gap, params Node?[] children)
        => new(vertical: true, gap: gap, padding: 0, children: children);

    /// <summary>Vertical stack (<see cref="Column"/>) with default 12px gap.</summary>
    public static StackBuilder VStack(params Node?[] children)
        => new(vertical: true, gap: Space.Md, padding: 0, children: children);

    /// <summary>Horizontal stack (<see cref="Row"/>). Supports both <c>HStack(a, b)</c> and <c>HStack(gap: Space.Sm)[ a, b ]</c>.</summary>
    public static StackBuilder HStack(double gap, params Node?[] children)
        => new(vertical: false, gap: gap, padding: 0, children: children);

    /// <summary>Horizontal stack (<see cref="Row"/>) with default 12px gap.</summary>
    public static StackBuilder HStack(params Node?[] children)
        => new(vertical: false, gap: Space.Md, padding: 0, children: children);

    /// <summary>Multi-column grid (<see cref="DigitalBrain.UI.Grid"/>).</summary>
    public static GridBuilder Grid(int columns, double gap = Space.Md, params Node?[] children)
        => new(columns, gap, children);

    /// <summary>Two-pane desktop split view (<see cref="SplitPane"/>).</summary>
    public static SplitPane Split(Node leading, Node trailing, double leadingExtent = 280, double gap = Space.Lg)
    {
        ArgumentNullException.ThrowIfNull(leading);
        ArgumentNullException.ThrowIfNull(trailing);
        return new SplitPane(leading, trailing, leadingExtent, gap);
    }

    /// <summary>Responsive two-pane split view (<see cref="SplitPane"/>) collapsing cleanly on narrow viewports in the renderer.</summary>
    public static SplitPane ResponsiveSplit(Node leading, Node trailing, double leadingExtent = 260, double gap = Space.Lg)
        => Split(leading, trailing, leadingExtent, gap);

    /// <summary>Elevated card container (<see cref="DigitalBrain.UI.Card"/>). Supports <c>Card("Title", "Subtitle")[ ... ]</c>.</summary>
    public static CardBuilder Card(string title, string subtitle = "", params Node?[] children)
        => new(title, subtitle, children);

    /// <summary>Materiality surface container (<see cref="DigitalBrain.UI.Surface"/>).</summary>
    public static SurfaceBuilder Surface(
        string kind = SurfaceKind.Elevated,
        string? title = null,
        string? subtitle = null,
        double padding = 18,
        params Node?[] children)
        => new(kind, title, subtitle, padding, children);

    /// <summary>Standard window/section header bar pairing a display heading with trailing action controls.</summary>
    public static Row HeaderBar(string title, string? subtitle = null, params Node?[] actions)
    {
        var trailing = StackBuilder.FilterNulls(actions);
        if (trailing.Count == 0)
        {
            return new Row([new Heading(title, 2, subtitle)]) { Gap = Space.Md };
        }

        return new Row([new Heading(title, 2, subtitle), new Spacer(0), .. trailing]) { Gap = Space.Sm };
    }

    /// <summary>Flexible horizontal/vertical spacer (<c>size: 0</c>) that expands to push trailing items to the far edge.</summary>
    public static Spacer FlexSpacer()
        => new(0);

    /// <summary>Left-aligned uppercase eyebrow section header (<c>level: 6</c>) that never clips inside narrow sidebars.</summary>
    public static Heading SectionHeader(string title, string? subtitle = null)
        => new(title, 6, subtitle);

    /// <summary>Compact WinUI-style command toolbar strip (<c>Surface("toolbar")</c>).</summary>
    public static Surface Toolbar(params Node?[] items)
        => new(StackBuilder.FilterNulls(items), "toolbar", Padding: 8);

    /// <summary>WinUI-style navigation breadcrumb bar separating items with chevron dividers (<c>Surface("breadcrumb")</c>).</summary>
    public static Surface Breadcrumb(params Node?[] segments)
        => new(StackBuilder.FilterNulls(segments), "breadcrumb", Padding: 0);

    /// <summary>Crisp desktop segmented pill switcher (<see cref="Select"/> with <c>segmented:</c> wire convention).</summary>
    public static Select Segmented(string selectedId, params OptionBuilder[] options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new Select("segmented:", selectedId, [.. options.Select(o => o.ToSelectOption())]);
    }

    /// <summary>Crisp desktop segmented pill switcher with an inline caption label.</summary>
    public static Select Segmented(string label, string selectedId, params OptionBuilder[] options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var wireLabel = string.IsNullOrWhiteSpace(label) ? "segmented:" : $"segmented:{label}";
        return new Select(wireLabel, selectedId, [.. options.Select(o => o.ToSelectOption())]);
    }

    /// <summary>Raster/URL image canvas supporting both <c>data:image/*;base64,...</c> inline previews and remote URLs.</summary>
    public static Surface ImageView(string src, string caption = "", double height = 320, string fit = "contain")
        => new([], $"image:{fit}", caption, src, height);

    /// <summary>Recessed monospace code viewer with line numbers and one-click copy (<c>Surface("code:...")</c>).</summary>
    public static Surface CodeBlock(string title, string code, string language = "csharp", double maxHeight = 360)
        => new([], $"code:{language}", title, code, maxHeight);

    /// <summary>Collapsible WinUI-style disclosure panel (<c>Surface("expander:...")</c>).</summary>
    public static Surface Expander(string title, string? subtitle = null, bool expanded = false, params Node?[] children)
        => new(StackBuilder.FilterNulls(children), expanded ? "expander:open" : "expander:closed", title, subtitle, 12);

    /// <summary>Bounded scrollable container (<c>Surface("scroll")</c>).</summary>
    public static Surface ScrollBox(Node child, double maxHeight = 360)
    {
        ArgumentNullException.ThrowIfNull(child);
        return new Surface([child], "scroll", Padding: maxHeight);
    }

    /// <summary>Live App Studio device/viewport frame (<c>Surface("studio-frame:...")</c>) for previewing mounted apps or units.</summary>
    public static Surface StudioFrame(string title, string? subtitle, double? viewportWidth, params Node?[] children)
    {
        var kind = viewportWidth is > 0
            ? $"studio-frame:{viewportWidth.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            : "studio-frame";
        return new Surface(StackBuilder.FilterNulls(children), kind, title, subtitle, 16);
    }

    /// <summary>Horizontal tab bar (<see cref="DigitalBrain.UI.Tabs"/>).</summary>
    public static Tabs Tabs(string selectedId, params TabBuilder[] items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new Tabs([.. items.Select(i => i.ToTabItem())], selectedId);
    }

    /// <summary>Defines a single tab item inside <see cref="Tabs(string, TabBuilder[])"/>.</summary>
    public static TabBuilder Tab(string id, string label, string? icon = null)
        => new(id, label, icon);

    /// <summary>Fixed spacer (<see cref="DigitalBrain.UI.Spacer"/>).</summary>
    public static Spacer Spacer(double size = Space.Md)
        => new(size);

    /// <summary>Hairline section divider (<see cref="DigitalBrain.UI.Divider"/>).</summary>
    public static Divider Divider(string? label = null)
        => new(label);

    // ── Content & Status (DESIGN.md §2.5) ────────────────────────────────────

    /// <summary>Body text (<see cref="DigitalBrain.UI.Text"/>).</summary>
    public static TextBuilder Text(string value)
        => new(value);

    /// <summary>Muted secondary text (<c>tone: "muted"</c>).</summary>
    public static TextBuilder MutedText(string value)
        => new(value, "muted");

    /// <summary>Markdown text (<see cref="DigitalBrain.UI.Markdown"/>): headings, lists, emphasis, links and code drawn by the client.</summary>
    public static Markdown Markdown(string value) => new(value ?? string.Empty);

    /// <summary>Display heading (<see cref="DigitalBrain.UI.Heading"/>).</summary>
    public static Heading Heading(string value, int level = 2, string? subtitle = null)
        => new(value, level, subtitle);

    /// <summary>
    /// Accessible semantic status pill (<c>DESIGN.md</c> §2.5: "Status Is Never Colour Alone")
    /// pairing every <see cref="StatusKind"/> with its canonical tone and Lucide icon prefix.
    /// </summary>
    public static TagPill StatusPill(StatusKind status, string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        (string tone, string? icon) = status switch
        {
            StatusKind.Done => ("done", Icons.Check),
            StatusKind.Working => ("working", Icons.LoaderCircle),
            StatusKind.Error => ("error", Icons.TriangleAlert),
            StatusKind.NeedsYou => ("accent", Icons.Hand),
            _ => ("neutral", null),
        };
        return new TagPill(label, tone, icon);
    }

    /// <summary>Alias for <see cref="StatusPill(StatusKind, string)"/> aligned with the UI Kit component catalogue.</summary>
    public static TagPill StatusChip(StatusKind status, string label)
        => StatusPill(status, label);

    /// <summary>Tabular-numeral (<c>tnum</c>) KPI stat card (<see cref="StatCardNode"/>).</summary>
    public static StatCardNode Metric(
        string label,
        string value,
        string? delta = null,
        BadgeTone tone = BadgeTone.Neutral,
        string? icon = null,
        string? key = null,
        string automationName = "")
        => new(key ?? ButtonBuilder.SlugifyKey("metric", label), label, value, delta, tone, icon, automationName);

    /// <summary>Creates a tabular-numeral (<c>tnum</c>) KPI stat card (forwards to <see cref="DigitalBrain.UI.Ui.StatCard"/>).</summary>
    public static StatCardNode StatCard(
        string key,
        string label,
        string value,
        string? delta = null,
        BadgeTone tone = BadgeTone.Neutral,
        string? icon = null,
        string automationName = "")
        => Ui.StatCard(key, label, value, delta, tone, icon, automationName);

    /// <summary>Creates a studio-grade desktop app shell layout (forwards to <see cref="DigitalBrain.UI.Ui.AppShell"/>).</summary>
    public static AppShellNode AppShell(
        string key,
        Node? sidebar,
        Node body,
        Node? toolbar = null,
        Node? inspector = null,
        string automationName = "",
        string transition = "",
        bool edgeToEdge = false)
        => Ui.AppShell(key, sidebar, body, toolbar, inspector, automationName, transition, edgeToEdge);

    /// <summary>Builds a <see cref="Command"/> dispatching typed <see cref="UiNavigateTo"/> arrival arguments to <paramref name="target"/> (via <see cref="UiCommand"/> for universal manifest compatibility).</summary>
    public static Command NavigateToCommand<TArgs>(
        TArgs args,
        string? fromNeuron = null,
        string transition = ViewTransition.FadeThrough,
        string? correlationId = null,
        string target = ".")
    {
        var argsJson = args is null
            ? null
            : DigitalBrain.Invocation.RuntimeProtocol.SerializeToElement(args, typeof(TArgs)).GetRawText();
        var navPayload = new UiCommand(
            Target: "navigate-to",
            Method: UiNavigateTo.SignalAlias,
            Args:
            [
                DigitalBrain.Invocation.RuntimeProtocol.SerializeToElement(argsJson, typeof(string)),
                DigitalBrain.Invocation.RuntimeProtocol.SerializeToElement(fromNeuron, typeof(string)),
                DigitalBrain.Invocation.RuntimeProtocol.SerializeToElement(transition, typeof(string)),
                DigitalBrain.Invocation.RuntimeProtocol.SerializeToElement(correlationId, typeof(string)),
            ]);
        return new Command(
            string.IsNullOrWhiteSpace(target) ? "." : target,
            UiCommand.SignalAlias,
            DigitalBrain.Invocation.RuntimeProtocol.SerializeToElement(navPayload, typeof(UiCommand)));
    }

    /// <summary>Builds a <see cref="Command"/> dispatching <see cref="UiNavigateFrom"/> departure checks to <paramref name="target"/> (via <see cref="UiCommand"/> for universal manifest compatibility).</summary>
    public static Command NavigateFromCommand(
        string? toNeuron = null,
        string? route = null,
        bool force = false,
        string target = ".")
    {
        var navPayload = new UiCommand(
            Target: "navigate-from",
            Method: UiNavigateFrom.SignalAlias,
            Args:
            [
                DigitalBrain.Invocation.RuntimeProtocol.SerializeToElement(toNeuron, typeof(string)),
                DigitalBrain.Invocation.RuntimeProtocol.SerializeToElement(route, typeof(string)),
                DigitalBrain.Invocation.RuntimeProtocol.SerializeToElement(force, typeof(bool)),
            ]);
        return new Command(
            string.IsNullOrWhiteSpace(target) ? "." : target,
            UiCommand.SignalAlias,
            DigitalBrain.Invocation.RuntimeProtocol.SerializeToElement(navPayload, typeof(UiCommand)));
    }

    /// <summary>Creates a sidebar navigation item (forwards to <see cref="DigitalBrain.UI.Ui.NavItem"/>).</summary>
    [System.Runtime.CompilerServices.OverloadResolutionPriority(1)]
    public static NavItemNode NavItem(
        string key,
        string label,
        string icon,
        string action,
        bool selected = false,
        string? badge = null,
        Command? command = null,
        string automationName = "")
        => Ui.NavItem(key, label, icon, action, selected, badge, command, string.IsNullOrWhiteSpace(automationName) ? label : automationName);

    /// <summary>Creates a sidebar navigation item converting a legacy <see cref="Emit"/> to a <see cref="Command"/> targeting <c>"."</c>.</summary>
    public static NavItemNode NavItem(
        string key,
        string label,
        string icon,
        string action,
        bool selected = false,
        string? badge = null,
        Emit? emit = null,
        string automationName = "")
        => Ui.NavItem(
            key,
            label,
            icon,
            action,
            selected,
            badge,
            emit is null ? null : new Command(".", emit.Signal, emit.Args),
            string.IsNullOrWhiteSpace(automationName) ? label : automationName);

    /// <summary>Creates an installed-app launcher tile (forwards to <see cref="DigitalBrain.UI.Ui.AppTile"/>).</summary>
    public static AppTileNode AppTile(
        string key,
        string appId,
        string title,
        string? icon = null,
        string? category = null,
        AppLaunch launch = AppLaunch.Windowed,
        int? dock = null,
        string appKind = "compiled",
        string? description = null,
        Command? command = null,
        string automationName = "")
        => Ui.AppTile(key, appId, title, icon, category, launch, dock, appKind, description, command, automationName);

    /// <summary>Creates a declarative WASM / Web studio browser surface (forwards to <see cref="DigitalBrain.UI.Ui.WebBrowser"/>).</summary>
    public static WebBrowser WebBrowser(
        string url,
        string? title = null,
        string key = "web-browser",
        double height = 520,
        string executionMode = "wasm-ui",
        string automationName = "",
        string chrome = "full")
        => Ui.WebBrowser(url, title, key, height, executionMode, automationName, chrome);

    /// <summary>Honest empty-state prompt (<see cref="EmptyStateNode"/>) with optional suggestion buttons.</summary>
    public static EmptyStateNode Empty(string title, string? body = null, params Button[] suggestions)
        => new(title, body, suggestions.Length == 0 ? null : suggestions);

    /// <summary>Inline status or warning banner (<see cref="InfoBar"/>); a closable one names its <paramref name="key"/> and <paramref name="close"/> command.</summary>
    public static InfoBar Banner(string title, string message, string tone = "neutral", Button? action = null, Command? close = null, string key = "")
        => new(title, message, tone, action, close, key);

    // ── Interactive Controls ─────────────────────────────────────────────────

    /// <summary>The single primary CTA button of a surface (<c>style: "primary"</c>, <c>tone: "accent"</c>).</summary>
    public static ButtonBuilder PrimaryButton(string label, string? icon = null)
        => new(label, style: "primary", tone: ButtonTone.Accent, icon: icon);

    /// <summary>Standard secondary button (<c>style: "secondary"</c>, <c>tone: "neutral"</c>).</summary>
    public static ButtonBuilder Button(string label, string? icon = null)
        => new(label, style: "secondary", tone: ButtonTone.Neutral, icon: icon);

    /// <summary>Quiet borderless ghost button (<c>style: "ghost"</c>).</summary>
    public static ButtonBuilder GhostButton(string label, string? icon = null)
        => new(label, style: "ghost", tone: ButtonTone.Neutral, icon: icon);

    /// <summary>Destructive action button (<c>style: "danger"</c>).</summary>
    public static ButtonBuilder DangerButton(string label, string? icon = null)
        => new(label, style: "danger", tone: ButtonTone.Neutral, icon: icon);

    /// <summary>An icon-only button; <paramref name="tooltip"/> says what it does (name it: <c>IconButton(icon, tooltip: "...")</c>).</summary>
    public static IconButtonBuilder IconButton(string icon, string tooltip) => new(icon, tooltip);

    /// <summary>Replaced: an icon button names its tooltip.</summary>
    [Obsolete("An icon button says what it does: IconButton(icon, tooltip: \"...\").", error: true)]
    public static IconButtonBuilder IconButton(string icon) => throw new NotSupportedException("IconButton(icon, tooltip:)");

    /// <summary>Single-line editable text field (<see cref="FieldKind.Text"/>).</summary>
    public static FieldBuilder TextField(string label, string? placeholder = null)
        => new(label, FieldKind.Text, placeholder);

    /// <summary>Multiline text editor field (<see cref="FieldKind.Multiline"/>).</summary>
    public static FieldBuilder MultilineField(string label, string? placeholder = null)
        => new(label, FieldKind.Multiline, placeholder);

    /// <summary>Numeric input field (<see cref="FieldKind.Number"/>).</summary>
    public static FieldBuilder NumberField(string label, string? placeholder = null)
        => new(label, FieldKind.Number, placeholder);

    /// <summary>Search input box (<see cref="FieldKind.Search"/>).</summary>
    public static FieldBuilder SearchField(string placeholder = "Search…", string label = "Search")
        => new(label, FieldKind.Search, placeholder);

    /// <summary>Masked secret/API-key field (<see cref="FieldKind.Secret"/>), never echoed back.</summary>
    public static FieldBuilder SecretField(string label, string? placeholder = null)
        => new(label, FieldKind.Secret, placeholder);

    /// <summary>Dropdown / segmented selector (<see cref="Select"/>).</summary>
    public static Select SelectBox(string label, string selectedId, params OptionBuilder[] options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new Select(label, selectedId, [.. options.Select(o => o.ToSelectOption())]);
    }

    /// <summary>Defines an option inside <see cref="SelectBox(string, string, OptionBuilder[])"/>.</summary>
    public static OptionBuilder Option(string id, string label)
        => new(id, label);

    /// <summary>36x20px pill toggle switch (<see cref="DigitalBrain.UI.Switch"/>).</summary>
    public static ToggleBuilder ToggleSwitch(string label, bool isOn, bool locked = false)
        => new(label, isOn, locked);

    /// <summary>36x20px pill toggle switch (<see cref="DigitalBrain.UI.Switch"/>).</summary>
    public static ToggleBuilder Switch(string label, bool isOn, bool locked = false)
        => new(label, isOn, locked);

    // ── Rich Cards & Conversation ────────────────────────────────────────────

    /// <summary>Pre-filled inline approval card (<see cref="ApprovalCardNode"/>).</summary>
    public static ApprovalCardNode ApprovalCard(
        string key,
        string id,
        string title,
        string summary,
        IReadOnlyList<ApprovalRowNode> rows,
        string valueLabel = "Value",
        string systemName = "DigitalBrain",
        string status = "pending",
        Resolve? resolve = null,
        string? sourceNeuronUri = null,
        string? icon = null,
        string? capabilityLabel = null,
        string automationName = "")
        => new(key, id, title, summary, rows, valueLabel, systemName, status, resolve, sourceNeuronUri, icon, capabilityLabel, automationName);

    /// <summary>Disambiguation choice card (<see cref="ChoiceCardNode"/>).</summary>
    public static ChoiceCardNode ChoiceCard(
        string id,
        string question,
        IReadOnlyList<ChoiceOptionNode> options,
        int? pickedIndex = null)
        => new(id, question, options, pickedIndex);

    /// <summary>Conversation timeline and composer (<see cref="DigitalBrain.UI.Conversation"/>).</summary>
    public static DigitalBrain.UI.Conversation Conversation(
        string key,
        IReadOnlyList<MessageNode> messages,
        ComposerNode composer,
        Node? waiting = null,
        string status = "ready",
        ErrorLineNode? error = null,
        DigitalBrain.UI.Button? stop = null,
        EmptyStateNode? empty = null,
        string automationName = "")
        => new(key, messages, composer, waiting, status, error, stop, empty, automationName);

    /// <summary>Structured code diff preview card (<see cref="DiffCardNode"/>).</summary>
    public static DiffCardNode DiffCard(
        string id,
        string repo,
        string targetBranch,
        string headSha,
        int filesChanged,
        int additions,
        int deletions,
        IReadOnlyList<DiffFileNode> files)
        => Ui.DiffCard(id, repo, targetBranch, headSha, filesChanged, additions, deletions, files);

    /// <summary>Pre-filled outbound message draft card (<see cref="DraftCardNode"/>).</summary>
    public static DraftCardNode DraftCard(
        string id,
        string channel,
        string to,
        string body,
        string from,
        string? subject = null,
        string status = "pending",
        DigitalBrain.UI.Button? send = null,
        DigitalBrain.UI.Button? discard = null)
        => new(id, channel, to, body, from, subject, status, send, discard);

    /// <summary>Inline recoverable error line (<see cref="ErrorLineNode"/>).</summary>
    public static ErrorLineNode ErrorLine(
        string what,
        string? detail = null,
        DigitalBrain.UI.Button? action = null)
        => new(what, detail, action);

    /// <summary>Multi-step background job progress card (<see cref="JobCardNode"/>).</summary>
    public static JobCardNode JobCard(
        string title,
        IReadOnlyList<JobStepNode> steps,
        double progress = 0,
        string status = "running",
        string? resultText = null,
        DigitalBrain.UI.Button? cancel = null)
        => new(title, steps, progress, status, resultText, cancel);

    /// <summary>Micro-app telemetry tile (<see cref="MetricTileNode"/>).</summary>
    public static MetricTileNode MetricTile(
        string id,
        string appId,
        string title,
        string value,
        string? delta = null,
        string deltaTone = "neutral",
        string? statusLabel = null,
        string statusTone = "neutral",
        IReadOnlyList<double>? sparkline = null,
        double? threshold = null,
        IReadOnlyList<DigitalBrain.UI.Button>? actions = null)
        => Ui.MetricTile(id, appId, title, value, delta, deltaTone, statusLabel, statusTone, sparkline, threshold, actions);

    /// <summary>Structured record summary card (<see cref="RecordCardNode"/>).</summary>
    public static RecordCardNode RecordCard(
        string objectLabel,
        string name,
        IReadOnlyList<RecordFieldNode> fields,
        string? subtitle = null,
        string linkLabel = "Open Neuron",
        DigitalBrain.UI.Button? open = null)
        => new(objectLabel, name, fields, subtitle, linkLabel, open);

    /// <summary>Secure credential request card (<see cref="SecretCardNode"/>).</summary>
    public static SecretCardNode SecretCard(
        string name,
        string why,
        string savedIn = "Variables and secrets",
        string status = "pending",
        DigitalBrain.UI.Button? enter = null,
        DigitalBrain.UI.Button? skip = null)
        => new(name, why, savedIn, status, enter, skip);

    // ── Composition (Slots) ──────────────────────────────────────────────────

    /// <summary>
    /// Mounts another live neuron's view or surface inside this tree (<see cref="NeuronRef"/>),
    /// enabling recursive micro-frontend composition across compiled and runtime units.
    /// </summary>
    public static NeuronRef Slot(string neuronUri, string refKind = "view")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(neuronUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(refKind);
        return new NeuronRef(refKind, neuronUri);
    }

    // ── Data & Topology ──────────────────────────────────────────────────────

    /// <summary>Sortable tabular data card (<see cref="DataTable"/>).</summary>
    public static DataTable Table(
        string title,
        IReadOnlyList<string> columns,
        IReadOnlyList<IReadOnlyList<string>> rows,
        string? sourceNeuronUri = null)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);
        return new DataTable(title, columns, rows, sourceNeuronUri);
    }

    /// <summary>Single-series or comparison line chart (<see cref="Chart"/> with <c>chartKind: "line"</c>).</summary>
    public static Chart LineChart(
        string title,
        IReadOnlyList<(string Label, double Value)> points,
        string seriesName = "",
        string prefix = "")
    {
        ArgumentNullException.ThrowIfNull(points);
        return new Chart("line", title, [.. points.Select(p => new ChartPointNode(p.Label, p.Value))], SeriesName: seriesName, Prefix: prefix);
    }

    /// <summary>Column / bar chart (<see cref="Chart"/> with <c>chartKind: "columns"</c>).</summary>
    public static Chart BarChart(
        string title,
        IReadOnlyList<(string Label, double Value)> points,
        string seriesName = "",
        string prefix = "")
    {
        ArgumentNullException.ThrowIfNull(points);
        return new Chart("columns", title, [.. points.Select(p => new ChartPointNode(p.Label, p.Value))], SeriesName: seriesName, Prefix: prefix);
    }

    /// <summary>
    /// Renders an unfolded <see cref="StateGraph{TState}"/> produced by <see cref="AggregateExplorer{TState}"/>
    /// as an interactive <see cref="BrainSceneNode"/> graph view.
    /// </summary>
    public static BrainSceneNode StateGraphView<TState>(
        StateGraph<TState> graph,
        string? selectedStateId = null,
        bool includeRejectedSelfLoops = true) where TState : class
    {
        ArgumentNullException.ThrowIfNull(graph);
        return graph.ToScene(selectedStateId, includeRejectedSelfLoops);
    }

    /// <summary>Normalizes any pending <see cref="Emit.Owner"/> placeholders inside <paramref name="node"/> to <paramref name="owner"/>.</summary>
    public static Node BindOwner(Node node, string owner)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        return NodeOwnerNormalizer.Normalize(node, owner);
    }
}
