namespace DigitalBrain.UI.Controls;

/// <summary>The state of a check box neuron.</summary>
public sealed record CheckBoxState
{
    /// <summary>The text beside the box.</summary>
    [Showcase("""{"kind":"checkBoxView","key":"terms","label":"I accept the terms","isOn":false,"isEnabled":true,"automationName":"Accept the terms"}""")]
    [Showcase("""{"kind":"checkBoxView","key":"locked","label":"Managed by your organisation","isOn":true,"isEnabled":false,"automationName":"Managed setting"}""")]
    public string Label { get; init; } = "";

    /// <summary>True when checked.</summary>
    public bool IsOn { get; init; }

    /// <summary>True draws a box that can also show a mixed state; the neuron's commands still set on or off.</summary>
    public bool IsThreeState { get; init; }

    /// <summary>False refuses every command with <see cref="ControlRefusals.Disabled"/>.</summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>The accessible name; null falls back to the neuron's key.</summary>
    public string? AutomationName { get; init; }
}

/// <summary>What a check box neuron draws: plain data; the client sends its commands to the neuron (".").</summary>
/// <param name="Key">The neuron's key, by which the client finds the control.</param>
/// <param name="Label">The text beside the box.</param>
/// <param name="IsOn">True when checked.</param>
/// <param name="IsEnabled">False draws it disabled.</param>
/// <param name="AutomationName">The accessible name.</param>
/// <param name="IsThreeState">True draws a box that can also show a mixed state.</param>
[ViewNode("checkBoxView")]
public sealed record CheckBoxView(string Key, string Label, bool IsOn, bool IsEnabled, string AutomationName, bool IsThreeState = false) : Node;
