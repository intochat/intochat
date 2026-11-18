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
/// The props of a control that selects one of its items. The parent checks a <c>Select(Id)</c> routed to the slot against them before the
/// child is reached, because the items are the parent's: the child stores only the selected id.
/// </summary>
public interface ISelectionProps
{
    /// <summary>The refusal reason of selecting <paramref name="id"/>, or null when it may be selected.</summary>
    /// <param name="id">The item id the command selects; null when the command names none.</param>
    string? RefusalOf(string? id);
}

/// <summary>The refusal reasons of a selection routed to a slot.</summary>
public static class SelectionRefusals
{
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
    : ControlProps(IsEnabled, AutomationName), ISelectionProps
{
    /// <inheritdoc />
    public string? RefusalOf(string? id) => Items.Any(item => item.Id == id) ? null : SelectionRefusals.UnknownItem;

    /// <inheritdoc />
    public bool Equals(TabStripProps? other) => other is not null && base.Equals(other) && Items.SequenceEqual(other.Items);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(base.GetHashCode(), Items.Count);
}
