using System.Linq.Expressions;
using DigitalBrain.Rules;
using DigitalBrain.UI;

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

/// <summary>Fluent container builder supporting both variadic construction and C# indexer child syntax (<c>VStack(...)[ a, b ]</c>).</summary>
public sealed class StackBuilder : KitNode
{
    private readonly bool _vertical;
    private double _gap;
    private double _padding;
    private IReadOnlyList<double>? _extents;
    private IReadOnlyList<Node> _children;

    internal StackBuilder(bool vertical, double gap, double padding, IReadOnlyList<Node?> children)
    {
        _vertical = vertical;
        _gap = gap;
        _padding = padding;
        _children = FilterNulls(children);
    }

    public StackBuilder this[params Node?[] children]
    {
        get
        {
            _children = FilterNulls(children);
            return this;
        }
    }

    public StackBuilder Gap(double gap)
    {
        _gap = gap;
        return this;
    }

    public StackBuilder Padding(double padding)
    {
        _padding = padding;
        return this;
    }

    public StackBuilder Extents(params double[] extents)
    {
        _extents = extents;
        return this;
    }

    public override Node ToNode() => _vertical
        ? new Column(_children) { Gap = _gap, Padding = _padding, Extents = _extents }
        : new Row(_children) { Gap = _gap, Padding = _padding, Extents = _extents };

    internal static IReadOnlyList<Node> FilterNulls(IEnumerable<Node?>? items)
        => items is null ? [] : [.. items.OfType<Node>()];
}

/// <summary>Fluent multi-column grid builder supporting <c>Grid(cols: 3)[ ... ]</c>.</summary>
public sealed class GridBuilder : KitNode
{
    private readonly int _columns;
    private double _gap;
    private IReadOnlyList<Node> _children;

    internal GridBuilder(int columns, double gap, IReadOnlyList<Node?> children)
    {
        _columns = Math.Max(1, columns);
        _gap = gap;
        _children = StackBuilder.FilterNulls(children);
    }

    public GridBuilder this[params Node?[] children]
    {
        get
        {
            _children = StackBuilder.FilterNulls(children);
            return this;
        }
    }

    public GridBuilder Gap(double gap)
    {
        _gap = gap;
        return this;
    }

    public override Node ToNode() => new Grid(_columns, _children, _gap);
}

/// <summary>Fluent card builder supporting <c>Card("Title", "Subtitle")[ ... ]</c>.</summary>
public sealed class CardBuilder : KitNode
{
    private readonly string _title;
    private readonly string _body;
    private IReadOnlyList<Node> _children;
    private Node? _header;
    private Node? _footer;

    internal CardBuilder(string title, string body, IReadOnlyList<Node?> children)
    {
        _title = title ?? string.Empty;
        _body = body ?? string.Empty;
        _children = StackBuilder.FilterNulls(children);
    }

    public CardBuilder this[params Node?[] children]
    {
        get
        {
            _children = StackBuilder.FilterNulls(children);
            return this;
        }
    }

    /// <summary>Sets the node shown above the title.</summary>
    public CardBuilder Header(Node? header)
    {
        _header = header;
        return this;
    }

    /// <summary>Sets the node shown below the children, such as a row of buttons.</summary>
    public CardBuilder Footer(Node? footer)
    {
        _footer = footer;
        return this;
    }

    public override Node ToNode() => new Card(_title, _body, _children, _header, _footer);
}

/// <summary>Fluent materiality surface builder (<c>acrylic</c>, <c>elevated</c>, <c>recessed</c>, <c>subtle</c>).</summary>
public sealed class SurfaceBuilder : KitNode
{
    private readonly string _kind;
    private string? _title;
    private string? _subtitle;
    private double _padding;
    private IReadOnlyList<Node> _children;

    internal SurfaceBuilder(string kind, string? title, string? subtitle, double padding, IReadOnlyList<Node?> children)
    {
        _kind = kind;
        _title = title;
        _subtitle = subtitle;
        _padding = padding;
        _children = StackBuilder.FilterNulls(children);
    }

    public SurfaceBuilder this[params Node?[] children]
    {
        get
        {
            _children = StackBuilder.FilterNulls(children);
            return this;
        }
    }

    public SurfaceBuilder Title(string title, string? subtitle = null)
    {
        _title = title;
        _subtitle = subtitle ?? _subtitle;
        return this;
    }

    public SurfaceBuilder Padding(double padding)
    {
        _padding = padding;
        return this;
    }

    public override Node ToNode() => new Surface(_children, _kind, _title, _subtitle, _padding);
}

/// <summary>Fluent text builder supporting semantic tones, monospace, tabular figures, and explicit font size.</summary>
public sealed class TextBuilder : KitNode
{
    private readonly string _value;
    private string _tone;
    private bool _tabular;
    private bool _mono;
    private double? _size;

    internal TextBuilder(string value, string tone = "neutral")
    {
        _value = value ?? string.Empty;
        _tone = tone;
    }

    public TextBuilder Tone(string tone)
    {
        _tone = tone;
        return this;
    }

    public TextBuilder Muted()
    {
        _tone = "muted";
        return this;
    }

    public TextBuilder Strong()
    {
        _tone = "strong";
        return this;
    }

    public TextBuilder Tabular(bool tabular = true)
    {
        _tabular = tabular;
        return this;
    }

    public TextBuilder Mono(bool mono = true)
    {
        _mono = mono;
        return this;
    }

    public TextBuilder Size(double size)
    {
        _size = size;
        return this;
    }

    public override Node ToNode() => new Text(_value)
    {
        Tone = _tone,
        Tabular = _tabular,
        Mono = _mono,
        Size = _size,
    };
}

/// <summary>Fluent icon-only button builder: the same bindings as <see cref="ButtonBuilder"/>, built as an <see cref="DigitalBrain.UI.IconButton"/>.</summary>
public sealed class IconButtonBuilder
{
    private readonly string _icon;
    private readonly string _tooltip;
    private readonly ButtonBuilder _button;

    internal IconButtonBuilder(string icon, string tooltip)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(icon);
        ArgumentException.ThrowIfNullOrWhiteSpace(tooltip);
        _icon = icon;
        _tooltip = tooltip;
        _button = new ButtonBuilder(tooltip, style: "ghost", tone: ButtonTone.Neutral, icon: icon, small: true);
    }

    /// <summary>Overrides the deterministic node key.</summary>
    public IconButtonBuilder Key(string key) { _button.Key(key); return this; }

    /// <summary>Sets the accessible name; the key is used when none is set.</summary>
    public IconButtonBuilder AutomationName(string automationName) { _button.AutomationName(automationName); return this; }

    /// <summary>Explicitly disables (or enables) the button.</summary>
    public IconButtonBuilder Disabled(bool disabled = true) { _button.Disabled(disabled); return this; }

    /// <summary>Disables the button while <paramref name="rule"/> is broken (see <see cref="ButtonBuilder.Guard(Rule)"/>).</summary>
    public IconButtonBuilder Guard(Rule rule) { _button.Guard(rule); return this; }

    /// <summary>Binds a pure <c>TState -&gt; TState</c> aggregate command expression.</summary>
    public IconButtonBuilder OnClick<TState>(Expression<Func<TState, TState>> command) where TState : class { _button.OnClick(command); return this; }

    /// <summary>Binds a rule-checked <c>TState -&gt; Decision&lt;TState&gt;</c> aggregate command expression.</summary>
    public IconButtonBuilder OnClick<TState>(Expression<Func<TState, Decision<TState>>> command) where TState : class { _button.OnClick(command); return this; }

    /// <summary>Binds an async screen orchestration method (<c>screen =&gt; screen.SyncAsync()</c>).</summary>
    public IconButtonBuilder OnClickAsync<TScreen>(Expression<Func<TScreen, Task>> command) where TScreen : class { _button.OnClickAsync(command); return this; }

    /// <summary>Binds a classic <c>[On]</c> signal as a command to the view's own neuron (".").</summary>
    public IconButtonBuilder Emits(Emit emit) { _button.Emits(emit); return this; }

    /// <summary>Binds the <see cref="Command"/> a click sends.</summary>
    public IconButtonBuilder Sends(Command command) { _button.Sends(command); return this; }

    /// <summary>Binds a client-local <see cref="DigitalBrain.UI.Intent"/>.</summary>
    public IconButtonBuilder WithIntent(IntentKind kind, string? arg = null) { _button.WithIntent(kind, arg); return this; }

    /// <summary>Materializes this builder as an <see cref="DigitalBrain.UI.IconButton"/>.</summary>
    public IconButton ToIconButton()
    {
        var button = _button.ToButton();
        return new IconButton(button.Key, _icon, _tooltip, button.Command, button.Intent, button.Enabled, button.AutomationName);
    }

    /// <summary>Materializes this builder as a <see cref="Node"/>.</summary>
    public Node ToNode() => ToIconButton();

    public static implicit operator Node(IconButtonBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.ToIconButton();
    }
}

/// <summary>
/// Fluent interactive button builder supporting typed expression-tree command binding (<c>.OnClick(x =&gt; x.Withdraw(30m))</c>),
/// domain rule guards (<c>.Guard(rule)</c>), client-local intents, and visual styles.
/// </summary>
public sealed class ButtonBuilder
{
    private string? _explicitKey;
    private string? _derivedKey;
    private readonly string _label;
    private readonly string _style;
    private readonly string _tone;
    private string? _icon;
    private bool _small;
    private bool _enabled = true;
    private string? _guardMessage;
    private string? _tooltip;
    private Command? _command;
    private string? _automationName;
    private Intent? _intent;

    internal ButtonBuilder(string label, string style, string tone, string? icon = null, bool small = false)
    {
        _label = label ?? string.Empty;
        _style = style;
        _tone = tone;
        _icon = icon;
        _small = small;
    }

    /// <summary>Broken domain rule message when guarded by <see cref="Guard(Rule)"/> and the rule is currently broken; otherwise <c>null</c>.</summary>
    public string? GuardMessage => _guardMessage;

    /// <summary>Overrides the deterministic node key.</summary>
    public ButtonBuilder Key(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _explicitKey = key;
        return this;
    }

    /// <summary>Sets the Lucide icon slug shown inside the button.</summary>
    public ButtonBuilder Icon(string? icon)
    {
        _icon = icon;
        return this;
    }

    /// <summary>Renders the compact 30px desktop button height.</summary>
    public ButtonBuilder Small(bool small = true)
    {
        _small = small;
        return this;
    }

    /// <summary>Explicitly disables (or enables) the button.</summary>
    public ButtonBuilder Disabled(bool disabled = true)
    {
        if (disabled)
        {
            _enabled = false;
        }

        return this;
    }

    /// <summary>
    /// Binds a domain <see cref="Rule"/> directly to the button: disables the control when <see cref="Rule.Broken"/> is true
    /// and records <see cref="Rule.Message"/> on <see cref="GuardMessage"/> (and wire <c>guardReason</c> tooltip metadata).
    /// Direct API/MCP invocation still enforces the exact same rule.
    /// </summary>
    public ButtonBuilder Guard(Rule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (rule.Broken)
        {
            _enabled = false;
            _guardMessage = rule.Message;
        }

        return this;
    }

    /// <summary>Attaches a <c>DESIGN.md</c> §10.8 tooltip message rendered on hover/focus.</summary>
    public ButtonBuilder Tooltip(string? tooltip)
    {
        _tooltip = tooltip;
        return this;
    }

    /// <summary>Binds a pure <c>TState -&gt; TState</c> aggregate command expression.</summary>
    public ButtonBuilder OnClick<TState>(Expression<Func<TState, TState>> command) where TState : class
    {
        var bound = CommandBinding.FromState(command);
        _derivedKey = bound.CanonicalKey;
        _command = bound.ToCommand();
        _intent = null;
        return this;
    }

    /// <summary>Binds a rule-checked <c>TState -&gt; Decision&lt;TState&gt;</c> aggregate command expression.</summary>
    public ButtonBuilder OnClick<TState>(Expression<Func<TState, Decision<TState>>> command) where TState : class
    {
        var bound = CommandBinding.FromDecision(command);
        _derivedKey = bound.CanonicalKey;
        _command = bound.ToCommand();
        _intent = null;
        return this;
    }

    /// <summary>Binds an async screen orchestration method (<c>screen =&gt; screen.SyncAsync()</c>).</summary>
    public ButtonBuilder OnClickAsync<TScreen>(Expression<Func<TScreen, Task>> command) where TScreen : class
    {
        var bound = CommandBinding.FromScreen(command);
        _derivedKey = bound.CanonicalKey;
        _command = bound.ToCommand();
        _intent = null;
        return this;
    }

    /// <summary>Binds an async screen orchestration method invoked on <c>this</c> (<c>() =&gt; SyncAsync()</c>).</summary>
    public ButtonBuilder OnClick(Expression<Func<Task>> command)
    {
        var bound = CommandBinding.FromScreen(command);
        _derivedKey = bound.CanonicalKey;
        _command = bound.ToCommand();
        _intent = null;
        return this;
    }

    /// <summary>Binds a classic <c>[On]</c> signal: a button sends it as a <see cref="Command"/> to its own view's neuron (".");
    /// the emit's owner is not kept, since a button only commands the view it is in or that view's children.</summary>
    public ButtonBuilder Emits(Emit emit)
    {
        ArgumentNullException.ThrowIfNull(emit);
        return Sends(new Command(".", emit.Signal, emit.Args));
    }

    /// <summary>Binds the <see cref="Command"/> a click sends to a neuron address relative to the view's neuron.</summary>
    public ButtonBuilder Sends(Command command)
    {
        ArgumentNullException.ThrowIfNull(command);
        _command = command;
        _intent = null;
        return this;
    }

    /// <summary>Sets the accessible name; the key is used when none is set.</summary>
    public ButtonBuilder AutomationName(string automationName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(automationName);
        _automationName = automationName;
        return this;
    }

    /// <summary>Binds a client-local <see cref="DigitalBrain.UI.Intent"/> (copy, open window, open settings).</summary>
    public ButtonBuilder WithIntent(IntentKind kind, string? arg = null)
    {
        _intent = new Intent(kind, arg);
        _command = null;
        return this;
    }

    /// <summary>Materializes this builder directly as a typed <see cref="Button"/> record.</summary>
    public Button ToButton()
    {
        var key = _explicitKey ?? _derivedKey ?? SlugifyKey("btn", _label.Length > 0 ? _label : (_icon ?? "action"));
        var command = _command;
        var intent = _intent;
        if (command is null && intent is null)
        {
            // DigitalBrain.UI.Button requires either Command or Intent; provide a safe inert no-op UiCommand when unbound.
            command = new Command(".", UiCommand.SignalAlias);
        }

        if (command is not null && _guardMessage is not null)
        {
            command = BoundCommand.AttachHintMetadata(command, _guardMessage, tooltip: null);
        }

        return new Button(key, _label, command, intent, _enabled, _tone, _automationName ?? "", _tooltip)
        {
            Style = _style,
            Icon = _icon,
            Small = _small,
        };
    }

    /// <summary>Materializes this builder as a <see cref="Node"/>.</summary>
    public Node ToNode() => ToButton();

    public static implicit operator Button(ButtonBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.ToButton();
    }

    internal static string SlugifyKey(string prefix, string text)
    {
        Span<char> chars = stackalloc char[Math.Min(48, text.Length)];
        var len = 0;
        var prevDash = false;
        for (var i = 0; i < text.Length && len < chars.Length; i++)
        {
            var ch = text[i];
            if (char.IsLetterOrDigit(ch))
            {
                chars[len++] = char.ToLowerInvariant(ch);
                prevDash = false;
            }
            else if (!prevDash && len > 0)
            {
                chars[len++] = '-';
                prevDash = true;
            }
        }

        while (len > 0 && chars[len - 1] == '-')
        {
            len--;
        }

        return len == 0 ? prefix : $"{prefix}:{new string(chars[..len])}";
    }
}

/// <summary>
/// Fluent editable field builder supporting typed <see cref="Arg.Text"/> command binding via <c>.OnSubmit(...)</c> and <c>.OnChange(...)</c>.
/// </summary>
public sealed class FieldBuilder : KitNode
{
    private string? _explicitKey;
    private string? _derivedKey;
    private readonly string _name;
    private bool _hasExplicitLabel;
    private string? _explicitLabel;
    private string? _controlledValue;
    private string? _helper;
    private string? _error;
    private bool _liveChange;
    private readonly FieldKind _kind;
    private string? _placeholder;
    private Emit? _onSubmit;

    internal FieldBuilder(string name, FieldKind kind, string? placeholder = null)
    {
        _name = name ?? string.Empty;
        _kind = kind;
        _placeholder = placeholder;
    }

    public FieldBuilder Key(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _explicitKey = key;
        return this;
    }

    /// <summary>Sets the visible human label above the field (pass <c>""</c> to hide the label row on compact/toolbar inputs).</summary>
    public FieldBuilder Label(string label)
    {
        _hasExplicitLabel = true;
        _explicitLabel = label ?? string.Empty;
        return this;
    }

    /// <summary>Hides the top label row for compact toolbar / inline inputs.</summary>
    public FieldBuilder NoLabel() => Label(string.Empty);

    /// <summary>Binds the current value from <c>TState</c> so the field initializes and stays synchronized without boilerplate <c>IField</c> wiring.</summary>
    public FieldBuilder Value(string? value)
    {
        _controlledValue = value ?? string.Empty;
        return this;
    }

    /// <summary>Sets optional helper description text beneath the field.</summary>
    public FieldBuilder Description(string? description)
    {
        _helper = description;
        return this;
    }

    /// <summary>Sets an inline validation error message beneath the field.</summary>
    public FieldBuilder Error(string? error)
    {
        _error = error;
        return this;
    }

    /// <summary>Displays <see cref="Rule.Message"/> as an inline field validation error whenever <see cref="Rule.Broken"/> is true.</summary>
    public FieldBuilder Guard(Rule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (rule.Broken)
        {
            _error = rule.Message;
        }

        return this;
    }

    public FieldBuilder Placeholder(string? placeholder)
    {
        _placeholder = placeholder;
        return this;
    }

    /// <summary>Binds field submission (<c>fieldValue</c>) to a pure <c>TState -&gt; TState</c> command (e.g. <c>.OnSubmit(x =&gt; x.SetQuery(Arg.Text))</c>).</summary>
    public FieldBuilder OnSubmit<TState>(Expression<Func<TState, TState>> command) where TState : class
        => Bind(CommandBinding.FromState(command), live: false);

    /// <summary>Binds field submission (<c>fieldValue</c>) to a rule-checked <c>TState -&gt; Decision&lt;TState&gt;</c> command.</summary>
    public FieldBuilder OnSubmit<TState>(Expression<Func<TState, Decision<TState>>> command) where TState : class
        => Bind(CommandBinding.FromDecision(command), live: false);

    /// <summary>Binds field submission (<c>fieldValue</c>) to an async screen orchestration method.</summary>
    public FieldBuilder OnSubmit(Expression<Func<Task>> command)
        => Bind(CommandBinding.FromScreen(command), live: false);

    /// <summary>Binds debounced live typing changes (<c>fieldValue</c>) to a pure <c>TState -&gt; TState</c> command.</summary>
    public FieldBuilder OnChange<TState>(Expression<Func<TState, TState>> command) where TState : class
        => Bind(CommandBinding.FromState(command), live: true);

    /// <summary>Binds debounced live typing changes (<c>fieldValue</c>) to a rule-checked <c>TState -&gt; Decision&lt;TState&gt;</c> command.</summary>
    public FieldBuilder OnChange<TState>(Expression<Func<TState, Decision<TState>>> command) where TState : class
        => Bind(CommandBinding.FromDecision(command), live: true);

    /// <summary>Binds an explicit <see cref="DigitalBrain.UI.Emit"/>.</summary>
    public FieldBuilder Emits(Emit emit)
    {
        ArgumentNullException.ThrowIfNull(emit);
        _onSubmit = emit;
        return this;
    }

    private FieldBuilder Bind(BoundCommand bound, bool live)
    {
        _derivedKey = bound.CanonicalKey;
        _liveChange = _liveChange || live;
        _onSubmit = bound.ToEmit(CommandBinding.ActiveOwner);
        return this;
    }

    public override Node ToNode()
    {
        var key = _explicitKey ?? _derivedKey ?? ButtonBuilder.SlugifyKey("field", _name);
        var emit = BoundCommand.AttachFieldMetadata(
            _onSubmit,
            CommandBinding.ActiveOwner,
            _hasExplicitLabel,
            _explicitLabel,
            _controlledValue,
            _helper,
            _error,
            _liveChange);
        return new Field(key, _name, _kind, _placeholder, emit);
    }
}

/// <summary>Fluent toggle switch builder supporting command binding and domain rule guards.</summary>
public sealed class ToggleBuilder : KitNode
{
    private string? _explicitKey;
    private string? _derivedKey;
    private readonly string _label;
    private readonly bool _isOn;
    private bool _locked;
    private Emit? _emit;

    internal ToggleBuilder(string label, bool isOn, bool locked = false)
    {
        _label = label ?? string.Empty;
        _isOn = isOn;
        _locked = locked;
    }

    public ToggleBuilder Key(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _explicitKey = key;
        return this;
    }

    public ToggleBuilder Guard(Rule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (rule.Broken)
        {
            _locked = true;
        }

        return this;
    }

    public ToggleBuilder OnToggle<TState>(Expression<Func<TState, TState>> command) where TState : class
        => Bind(CommandBinding.FromState(command));

    public ToggleBuilder OnToggle<TState>(Expression<Func<TState, Decision<TState>>> command) where TState : class
        => Bind(CommandBinding.FromDecision(command));

    public ToggleBuilder OnToggle(Expression<Func<Task>> command)
        => Bind(CommandBinding.FromScreen(command));

    public ToggleBuilder OnChange<TState>(Expression<Func<TState, TState>> command) where TState : class
        => OnToggle(command);

    public ToggleBuilder OnChange<TState>(Expression<Func<TState, Decision<TState>>> command) where TState : class
        => OnToggle(command);

    public ToggleBuilder OnChange(Expression<Func<Task>> command)
        => OnToggle(command);

    public ToggleBuilder Emits(Emit emit)
    {
        ArgumentNullException.ThrowIfNull(emit);
        _emit = emit;
        return this;
    }

    private ToggleBuilder Bind(BoundCommand bound)
    {
        _derivedKey = bound.CanonicalKey;
        _emit = bound.ToEmit(CommandBinding.ActiveOwner);
        return this;
    }

    public override Node ToNode()
    {
        var key = _explicitKey ?? _derivedKey ?? ButtonBuilder.SlugifyKey("toggle", _label);
        return new Switch(key, _label, _isOn, _emit, _locked);
    }
}

/// <summary>Fluent tab item builder for <see cref="Kit.Tabs(string, TabBuilder[])"/>.</summary>
public sealed class TabBuilder
{
    private string? _explicitKey;
    private string? _derivedKey;
    private readonly string _id;
    private readonly string _label;
    private readonly string? _icon;
    private Emit? _emit;

    internal TabBuilder(string id, string label, string? icon = null)
    {
        _id = id;
        _label = label;
        _icon = icon;
    }

    public TabBuilder Key(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _explicitKey = key;
        return this;
    }

    public TabBuilder OnSelect<TState>(Expression<Func<TState, TState>> command) where TState : class
        => Bind(CommandBinding.FromState(command));

    public TabBuilder OnSelect<TState>(Expression<Func<TState, Decision<TState>>> command) where TState : class
        => Bind(CommandBinding.FromDecision(command));

    public TabBuilder Emits(Emit emit)
    {
        ArgumentNullException.ThrowIfNull(emit);
        _emit = emit;
        return this;
    }

    private TabBuilder Bind(BoundCommand bound)
    {
        _derivedKey = bound.CanonicalKey;
        _emit = bound.ToEmit(CommandBinding.ActiveOwner);
        return this;
    }

    public TabItem ToTabItem()
    {
        var key = _explicitKey ?? _derivedKey ?? $"tab:{_id}";
        var emit = CommandBinding.EnsureEmit(_emit, CommandBinding.ActiveOwner);
        return new TabItem(key, _id, _label, emit, _icon);
    }

    public static implicit operator TabItem(TabBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.ToTabItem();
    }
}

/// <summary>Fluent option builder for <see cref="Kit.SelectBox(string, string, OptionBuilder[])"/>.</summary>
public sealed class OptionBuilder
{
    private string? _explicitKey;
    private string? _derivedKey;
    private readonly string _id;
    private readonly string _label;
    private Emit? _emit;

    internal OptionBuilder(string id, string label)
    {
        _id = id;
        _label = label;
    }

    public OptionBuilder Key(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _explicitKey = key;
        return this;
    }

    public OptionBuilder OnSelect<TState>(Expression<Func<TState, TState>> command) where TState : class
        => Bind(CommandBinding.FromState(command));

    public OptionBuilder OnSelect<TState>(Expression<Func<TState, Decision<TState>>> command) where TState : class
        => Bind(CommandBinding.FromDecision(command));

    public OptionBuilder Emits(Emit emit)
    {
        ArgumentNullException.ThrowIfNull(emit);
        _emit = emit;
        return this;
    }

    private OptionBuilder Bind(BoundCommand bound)
    {
        _derivedKey = bound.CanonicalKey;
        _emit = bound.ToEmit(CommandBinding.ActiveOwner);
        return this;
    }

    public SelectOption ToSelectOption()
    {
        var key = _explicitKey ?? _derivedKey ?? $"opt:{_id}";
        return new SelectOption(key, _id, _label, _emit);
    }

    public static implicit operator SelectOption(OptionBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.ToSelectOption();
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
        string? key = null)
        => new(key ?? ButtonBuilder.SlugifyKey("metric", label), label, value, delta, tone, icon);

    /// <summary>Creates a tabular-numeral (<c>tnum</c>) KPI stat card (forwards to <see cref="DigitalBrain.UI.Ui.StatCard"/>).</summary>
    public static StatCardNode StatCard(
        string key,
        string label,
        string value,
        string? delta = null,
        BadgeTone tone = BadgeTone.Neutral,
        string? icon = null)
        => Ui.StatCard(key, label, value, delta, tone, icon);

    /// <summary>Creates a studio-grade desktop app shell layout (forwards to <see cref="DigitalBrain.UI.Ui.AppShell"/>).</summary>
    public static AppShellNode AppShell(
        string key,
        Node? sidebar,
        Node body,
        Node? toolbar = null,
        Node? inspector = null)
        => Ui.AppShell(key, sidebar, body, toolbar, inspector);

    /// <summary>Creates a sidebar navigation item (forwards to <see cref="DigitalBrain.UI.Ui.NavItem"/>).</summary>
    public static NavItemNode NavItem(
        string key,
        string label,
        string icon,
        string action,
        bool selected = false,
        string? badge = null,
        Emit? emit = null)
        => Ui.NavItem(key, label, icon, action, selected, badge, emit);

    /// <summary>Creates a declarative WASM / Web studio browser surface (forwards to <see cref="DigitalBrain.UI.Ui.WebBrowser"/>).</summary>
    public static WebBrowser WebBrowser(
        string url,
        string? title = null,
        string key = "web-browser",
        double height = 520,
        string executionMode = "wasm-ui")
        => Ui.WebBrowser(url, title, key, height, executionMode);

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

    /// <summary>36x20px pill toggle switch (<see cref="Switch"/>).</summary>
    public static ToggleBuilder ToggleSwitch(string label, bool isOn, bool locked = false)
        => new(label, isOn, locked);

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
