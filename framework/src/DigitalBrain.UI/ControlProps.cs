using System.Text.Json;
using System.Text.Json.Serialization;

namespace DigitalBrain.UI;

/// <summary>A control neuron whose props are <typeparamref name="TProps"/>: it ties <c>Slot&lt;TChild, TProps&gt;(key, props)</c> to the child's own props record.</summary>
/// <typeparam name="TProps">The props record the parent writes on the control's slot.</typeparam>
#pragma warning disable CA1040 // A marker: the constraint is the whole point.
public interface IControl<TProps> where TProps : ControlProps;
#pragma warning restore CA1040

/// <summary>
/// A control's props: the setup its parent decides, written on the slot node in the parent's view (<c>Slot&lt;TChild&gt;(key, props)</c>)
/// and re-rendered with the parent. The child neuron stores only what the user changes. A command routed through the parent to a slot
/// whose props are disabled is refused with <see cref="Disabled"/>.
/// </summary>
/// <param name="IsEnabled">False draws the control disabled, and the parent refuses every command routed to it.</param>
/// <param name="AutomationName">The accessible name; null falls back to the label or header, then the slot key.</param>
public abstract record ControlProps(bool IsEnabled = true, string? AutomationName = null)
{
    /// <summary>The refusal reason of a command to a slot whose props are disabled.</summary>
    public const string Disabled = "control.disabled";
}

/// <summary>The props of a check box: what its parent decides.</summary>
/// <param name="Label">The text beside the box.</param>
/// <param name="IsEnabled">False draws it disabled; its parent refuses commands to it.</param>
/// <param name="AutomationName">The accessible name; null falls back to the label.</param>
public sealed record CheckBoxProps(string Label, bool IsEnabled = true, string? AutomationName = null) : ControlProps(IsEnabled, AutomationName);

/// <summary>The props of a toggle switch: what its parent decides.</summary>
/// <param name="Label">The header beside the switch.</param>
/// <param name="OnContent">The text shown while on; null shows none.</param>
/// <param name="OffContent">The text shown while off; null shows none.</param>
/// <param name="IsEnabled">False draws it disabled; its parent refuses commands to it.</param>
/// <param name="AutomationName">The accessible name; null falls back to the label.</param>
public sealed record ToggleSwitchProps(string Label, string? OnContent = null, string? OffContent = null, bool IsEnabled = true, string? AutomationName = null)
    : ControlProps(IsEnabled, AutomationName);

/// <summary>The props of an expander: what its parent decides.</summary>
/// <param name="Header">The always-visible header.</param>
/// <param name="Children">The content shown while expanded (data nodes).</param>
/// <param name="IsEnabled">False draws it disabled; its parent refuses commands to it.</param>
/// <param name="AutomationName">The accessible name; null falls back to the header.</param>
public sealed record ExpanderProps(string Header, IReadOnlyList<Node> Children, bool IsEnabled = true, string? AutomationName = null)
    : ControlProps(IsEnabled, AutomationName);

/// <summary>
/// Props that check a command routed to their slot. The parent's Send asks them about every command before the child is reached, because
/// what the check needs (the items, the mode) is the parent's: the child stores only what the user changes.
/// </summary>
public interface ICommandGate
{
    /// <summary>The refusal reason of <paramref name="signal"/> with <paramref name="args"/>, or null to pass it to the child.</summary>
    /// <param name="signal">The command's signal alias.</param>
    /// <param name="args">The command's arguments as wire JSON, if any.</param>
    string? RefusalOf(string signal, JsonElement? args);
}

/// <summary>The refusal reasons of a selection routed to a slot.</summary>
public static class SelectionRefusals
{
    /// <summary>The alias of the select command (<c>DigitalBrain.UI.Commands.Select</c>, which this assembly cannot reference).</summary>
    public const string SelectSignal = "control.select";

    // The refusal of a select command whose id `refusal` judges; other signals pass.
    internal static string? OfSelect(string signal, JsonElement? args, Func<string?, string?> refusal)
        => signal != SelectSignal ? null
            : refusal(args is { ValueKind: JsonValueKind.Object } json && json.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null);

    /// <summary>The id names no item of the slot's props.</summary>
    public const string UnknownItem = "control.unknownItem";

    /// <summary>The slot's props allow no selection.</summary>
    public const string SelectionOff = "control.selectionOff";
}

/// <summary>An item of a tab strip.</summary>
/// <param name="Id">The id a selection names.</param>
/// <param name="Label">The tab's text.</param>
/// <param name="Icon">An optional icon name.</param>
public sealed record TabStripItem(string Id, string Label, string? Icon = null);

/// <summary>The props of a tab strip: what its parent decides.</summary>
/// <param name="Items">The tabs, in order.</param>
/// <param name="IsEnabled">False draws it disabled; its parent refuses commands to it.</param>
/// <param name="AutomationName">The accessible name; null falls back to the slot key.</param>
public sealed record TabStripProps(IReadOnlyList<TabStripItem> Items, bool IsEnabled = true, string? AutomationName = null)
    : ControlProps(IsEnabled, AutomationName), ICommandGate
{
    /// <inheritdoc />
    public string? RefusalOf(string signal, JsonElement? args)
        => SelectionRefusals.OfSelect(signal, args, id => Items.Any(item => item.Id == id) ? null : SelectionRefusals.UnknownItem);

    /// <inheritdoc />
    public bool Equals(TabStripProps? other) => other is not null && base.Equals(other) && Items.SequenceEqual(other.Items);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(base.GetHashCode(), Items.Count);
}

/// <summary>An option of a combo box.</summary>
/// <param name="Id">The id a selection names.</param>
/// <param name="Label">The option's text.</param>
public sealed record ComboBoxOption(string Id, string Label);

/// <summary>The props of a combo box: what its parent decides.</summary>
/// <param name="Header">The text above the box.</param>
/// <param name="Options">The options, in order.</param>
/// <param name="IsEnabled">False draws it disabled; its parent refuses commands to it.</param>
/// <param name="AutomationName">The accessible name; null falls back to the header.</param>
public sealed record ComboBoxProps(string Header, IReadOnlyList<ComboBoxOption> Options, bool IsEnabled = true, string? AutomationName = null)
    : ControlProps(IsEnabled, AutomationName), ICommandGate
{
    /// <inheritdoc />
    public string? RefusalOf(string signal, JsonElement? args)
        => SelectionRefusals.OfSelect(signal, args, id => Options.Any(option => option.Id == id) ? null : SelectionRefusals.UnknownItem);

    /// <inheritdoc />
    public bool Equals(ComboBoxProps? other) => other is not null && base.Equals(other) && Header == other.Header && Options.SequenceEqual(other.Options);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(base.GetHashCode(), Header, Options.Count);
}

/// <summary>An item of a list box.</summary>
/// <param name="Id">The id a selection names.</param>
/// <param name="Title">The item's main line.</param>
/// <param name="Subtitle">An optional muted second line.</param>
public sealed record ListBoxItem(string Id, string Title, string? Subtitle = null);

/// <summary>Whether a list box lets a person select an item.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ListSelectionMode>))]
public enum ListSelectionMode
{
    /// <summary>One item can be selected.</summary>
#pragma warning disable CA1720 // WinUI's name for the mode; the wire value is "Single".
    Single,
#pragma warning restore CA1720

    /// <summary>Nothing can be selected: the list only shows its items.</summary>
    None,
}

/// <summary>The props of a list box: what its parent decides.</summary>
/// <param name="Items">The items, in order.</param>
/// <param name="SelectionMode">Single lets one item be selected; None refuses every selection (control.selectionOff).</param>
/// <param name="IsEnabled">False draws it disabled; its parent refuses commands to it.</param>
/// <param name="AutomationName">The accessible name; null falls back to the slot key.</param>
public sealed record ListBoxProps(IReadOnlyList<ListBoxItem> Items, ListSelectionMode SelectionMode = ListSelectionMode.Single, bool IsEnabled = true, string? AutomationName = null)
    : ControlProps(IsEnabled, AutomationName), ICommandGate
{
    /// <inheritdoc />
    public string? RefusalOf(string signal, JsonElement? args) => SelectionRefusals.OfSelect(signal, args, id => SelectionMode == ListSelectionMode.None
        ? SelectionRefusals.SelectionOff
        : Items.Any(item => item.Id == id) ? null : SelectionRefusals.UnknownItem);

    /// <inheritdoc />
    public bool Equals(ListBoxProps? other) => other is not null && base.Equals(other) && SelectionMode == other.SelectionMode && Items.SequenceEqual(other.Items);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(base.GetHashCode(), SelectionMode, Items.Count);
}
