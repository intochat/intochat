using System.Linq.Expressions;
using DigitalBrain.Rules;
using DigitalBrain.UI;

namespace IntoChat.OS.UI;

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
    private Command? _onSubmit;
    private string? _automationName;

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

    /// <summary>Sets the accessible name; defaults cleanly from label, name, or placeholder when omitted.</summary>
    public FieldBuilder AutomationName(string automationName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(automationName);
        _automationName = automationName;
        return this;
    }

    /// <summary>Binds a classic <c>[On]</c> signal as a <see cref="Command"/> to the view's own neuron (".").</summary>
    public FieldBuilder Emits(Emit emit)
    {
        ArgumentNullException.ThrowIfNull(emit);
        return Sends(new Command(".", emit.Signal, emit.Args));
    }

    /// <summary>Binds the <see cref="Command"/> a field submission sends.</summary>
    public FieldBuilder Sends(Command command)
    {
        ArgumentNullException.ThrowIfNull(command);
        _onSubmit = command;
        return this;
    }

    private FieldBuilder Bind(BoundCommand bound, bool live)
    {
        _derivedKey = bound.CanonicalKey;
        _liveChange = _liveChange || live;
        _onSubmit = bound.ToCommand();
        return this;
    }

    public override Node ToNode()
    {
        var key = _explicitKey ?? _derivedKey ?? ButtonBuilder.SlugifyKey("field", _name);
        var command = BoundCommand.AttachFieldMetadata(
            _onSubmit,
            _hasExplicitLabel,
            _explicitLabel,
            _controlledValue,
            _helper,
            _error,
            _liveChange);
        var auto = ResolveFieldAutomationName(_automationName, _hasExplicitLabel, _explicitLabel, _name, _placeholder, key);
        return new Field(key, _name, _kind, _placeholder, command, auto);
    }

    internal static string ResolveFieldAutomationName(
        string? explicitAutomation,
        bool hasExplicitLabel,
        string? explicitLabel,
        string? name,
        string? placeholder,
        string key)
    {
        if (!string.IsNullOrWhiteSpace(explicitAutomation))
        {
            return explicitAutomation;
        }

        if (hasExplicitLabel)
        {
            if (!string.IsNullOrWhiteSpace(explicitLabel))
            {
                return explicitLabel;
            }

            if (!string.IsNullOrWhiteSpace(placeholder))
            {
                return placeholder;
            }
        }
        else if (!string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        if (!string.IsNullOrWhiteSpace(placeholder))
        {
            return placeholder;
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        return key;
    }
}
