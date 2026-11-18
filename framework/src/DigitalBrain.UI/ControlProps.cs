namespace DigitalBrain.UI;

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
