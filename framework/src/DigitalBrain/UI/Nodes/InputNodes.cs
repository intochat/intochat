using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DigitalBrain.UI;

[JsonConverter(typeof(JsonStringEnumConverter<FieldKind>))]
public enum FieldKind
{
    /// <summary>Single-line text.</summary>
    Text,

    /// <summary>A search box.</summary>
    Search,

    /// <summary>A number.</summary>
    Number,

    /// <summary>A password, masked as typed.</summary>
    Password,

    /// <summary>A secret value (token, key), masked and never echoed back.</summary>
    Secret,

    /// <summary>Text with suggestions.</summary>
    Suggest,

    /// <summary>Several lines of text.</summary>
    Multiline,
}

/// <summary>An editable field. Submitting it sends <paramref name="OnSubmit"/> with the typed text added as <c>value</c>.</summary>
/// <param name="Key">The node's key within its view, by which an activation names it.</param>
/// <param name="Name">The field's label or field-neuron key.</param>
/// <param name="Kind">What the field holds.</param>
/// <param name="Placeholder">Hint shown while empty.</param>
/// <param name="OnSubmit">The command a submit sends.</param>
/// <param name="AutomationName">The accessible name; empty falls back to <paramref name="Key"/>.</param>
[ViewNode("field")]
public sealed record Field(
    string Key,
    string Name,
    [property: JsonPropertyName("fieldKind")] FieldKind Kind,
    string? Placeholder = null,
    Command? OnSubmit = null,
    string AutomationName = "") : Node
{
    /// <summary>The accessible name: the given one, or <see cref="Key"/> when none was given.</summary>
    public string AutomationName { get; init; } = string.IsNullOrEmpty(AutomationName) ? Key : AutomationName;
}

/// <summary>A progress bar or ring with a label: plain data, no state of its own.</summary>
/// <param name="Label">What is progressing.</param>
/// <param name="Value">The fraction done, 0 to 1 (a percentage above 1 is read as 0 to 100); ignored while <paramref name="IsIndeterminate"/>.</param>
/// <param name="Detail">A muted line below, such as "3/7".</param>
/// <param name="IsIndeterminate">True shows activity without a known fraction.</param>
/// <param name="ShowError">True draws it in the error tone: the work stopped.</param>

/// <summary>The button tones the renderer knows (the os <c>ControlVisual.Tone</c>): only neutral and accent.</summary>
public static class ButtonTone
{
    /// <summary>The ordinary tone.</summary>
    public const string Neutral = "neutral";

    /// <summary>The single primary action of a surface.</summary>
    public const string Accent = "accent";
}

/// <summary>A button (the os <c>button</c> control): plain data with a <see cref="UI.Command"/>; pressing it sends the command to a neuron.</summary>
/// <param name="Key">The node's key within its view, by which an activation names it.</param>
/// <param name="Label">The label.</param>
/// <param name="Command">The command a click sends, to a neuron address relative to the view's neuron ("." is the view's own); exactly one of <paramref name="Command"/> and <paramref name="Intent"/> is given.</param>
/// <param name="Intent">The client-local intent a click performs.</param>
/// <param name="Enabled">False shows the button disabled.</param>
/// <param name="Tone"><see cref="ButtonTone.Neutral"/> or <see cref="ButtonTone.Accent"/>.</param>
/// <param name="AutomationName">The accessible name; empty falls back to <paramref name="Key"/>.</param>
/// <param name="Tooltip">Text shown on hover and focus.</param>
/// <param name="Visible">False hides the button.</param>
[ViewNode("button")]
public sealed record Button(
    [Showcase("""{"kind":"button","key":"save","label":"Save","command":{"target":".","signal":"Saved"},"tone":"accent","automationName":"Save"}""")]
    [Showcase("""{"kind":"button","key":"copy","label":"Copy link","intent":{"intentKind":"Copy","arg":"https://intochat.io"},"automationName":"Copy link","tooltip":"Copies the link"}""")]
    string Key,
    string Label,
    Command? Command = null,
    Intent? Intent = null,
    bool Enabled = true,
    string Tone = ButtonTone.Neutral,
    string AutomationName = "",
    string? Tooltip = null,
    [property: DefaultValue(true)] bool Visible = true) : Node
{
    // Not serialised (private); a button that commands and intends, or does neither, cannot be built.
    private readonly bool _oneAction = (Command is null) != (Intent is null)
        ? true
        : throw new ArgumentException("A button carries exactly one of Command and Intent.", nameof(Intent));

    // Reads stored views and fixtures written before buttons carried commands: a legacy "emit" ({owner, signal, args}) reads as a
    // command to the view's own neuron (".") with its signal and args (Command's reading constructor). Only "command" is ever written.
    [JsonConstructor]
    private Button(string key, string label, Command? command, Intent? intent, bool enabled, string tone, string automationName, string? tooltip, bool visible, Command? legacyEmit)
        : this(key, label, command ?? legacyEmit, intent, enabled, tone ?? ButtonTone.Neutral, automationName ?? "", tooltip, visible)
    {
    }

    // The legacy member the reading constructor binds; never written.
    [JsonInclude, JsonPropertyName("emit"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
#pragma warning disable CA1822 // an instance member, so the serializer binds the reading constructor's parameter to it
    private Command? LegacyEmit => null;
#pragma warning restore CA1822

    private readonly UiStyle _style = UiStyle.FromPreset("secondary");

    /// <summary>The accessible name: the given one, or <see cref="Key"/> when none was given.</summary>
    public string AutomationName { get; init; } = string.IsNullOrEmpty(AutomationName) ? Key : AutomationName;

    /// <summary>Visual style: either a legacy preset name (<c>primary</c>, <c>secondary</c>, <c>ghost</c>, <c>danger</c>, <c>link</c>) or a structured <see cref="UiStyle"/> authored via <see cref="Mix.Style()"/>.</summary>
    [JsonIgnore]
    public UiStyle Style
    {
        get => _style;
        init => _style = value ?? UiStyle.FromPreset("secondary");
    }

    /// <summary>Wire representation of <see cref="Style"/>: either a preset string or a compact <see cref="UiStyle"/> JSON object.</summary>
    [JsonPropertyName("style")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? StyleWire
    {
        get => _style.ToWireJsonElement();
        init => _style = UiStyle.FromWireJsonElement(value);
    }

    /// <summary>Optional Lucide icon slug.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Icon { get; init; }

    /// <summary>True for the compact 30px desktop button height.</summary>
    public bool Small { get; init; }
}

/// <summary>An icon-only button (the os <c>iconButton</c> control): plain data with a <see cref="UI.Command"/>, its tooltip the visible name.</summary>
/// <param name="Key">The node's key within its view, by which an activation names it.</param>
/// <param name="Icon">The Lucide icon slug drawn.</param>
/// <param name="Tooltip">Text shown on hover and focus; an icon button always says what it does.</param>
/// <param name="Command">The command a click sends, to a neuron address relative to the view's neuron ("." is the view's own); exactly one of <paramref name="Command"/> and <paramref name="Intent"/> is given.</param>
/// <param name="Intent">The client-local intent a click performs.</param>
/// <param name="Enabled">False shows the button disabled.</param>
/// <param name="AutomationName">The accessible name; empty falls back to <paramref name="Key"/>.</param>
/// <param name="Visible">False hides the button.</param>
[ViewNode("iconButton")]
public sealed record IconButton(
    [Showcase("""{"kind":"iconButton","key":"refresh","icon":"refresh-cw","tooltip":"Refresh","command":{"target":".","signal":"Refreshed"},"automationName":"Refresh"}""")]
    [Showcase("""{"kind":"iconButton","key":"copy-id","icon":"copy","tooltip":"Copy id","intent":{"intentKind":"Copy","arg":"neuron://owner/notes"},"automationName":"Copy id","enabled":false}""")]
    string Key,
    string Icon,
    string Tooltip,
    Command? Command = null,
    Intent? Intent = null,
    bool Enabled = true,
    string AutomationName = "",
    [property: DefaultValue(true)] bool Visible = true) : Node
{
    // Not serialised (private); an icon button that commands and intends, or does neither, cannot be built.
    private readonly bool _oneAction = (Command is null) != (Intent is null)
        ? true
        : throw new ArgumentException("An icon button carries exactly one of Command and Intent.", nameof(Intent));

    /// <summary>The accessible name: the given one, or <see cref="Key"/> when none was given.</summary>
    public string AutomationName { get; init; } = string.IsNullOrEmpty(AutomationName) ? Key : AutomationName;
}

/// <summary>What a client-local intent does.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<IntentKind>))]
public enum IntentKind
{
    /// <summary>Copy <see cref="Intent.Arg"/> to the clipboard.</summary>
    Copy,

    /// <summary>Open the window or URL named by <see cref="Intent.Arg"/>.</summary>
    OpenWindow,

    /// <summary>Open the settings, at the page named by <see cref="Intent.Arg"/> when given.</summary>
    OpenSettings,
}

/// <summary>What a button does in the client itself, without a round trip to the server.</summary>
/// <param name="Kind">What to do.</param>
/// <param name="Arg">The text, window or page the intent acts on.</param>
[ViewNode("intent")]
public sealed record Intent([property: JsonPropertyName("intentKind")] IntentKind Kind, string? Arg = null) : Node;

/// <summary>An approval card's resolution: the OS inbox surface answers plan <paramref name="PlanId"/> (approve or deny); never a <see cref="Command"/>.</summary>
/// <param name="PlanId">The confirmation plan id.</param>
public sealed record Resolve(string PlanId);

/// <summary>Sortable tabular data card with optional <c>neuron://</c> source URI.</summary>
