namespace DigitalBrain.UI.Controls;

/// <summary>The state of a check box neuron: only what the user changes.</summary>
public sealed record CheckBoxState
{
    /// <summary>True when checked.</summary>
    [Showcase("""{"kind":"checkBoxView","key":"terms","isOn":false,"props":{"label":"I accept the terms","isEnabled":true,"automationName":"Accept the terms"}}""")]
    [Showcase("""{"kind":"checkBoxView","key":"locked","isOn":true,"props":{"label":"Managed by your organisation","isEnabled":false,"automationName":"Managed setting"}}""")]
    public bool IsOn { get; init; }
}

/// <summary>What a check box neuron draws: its key and value. The client draws the label and enabled from the child's props.</summary>
/// <param name="Key">The neuron's key, by which the client finds the control.</param>
/// <param name="IsOn">True when checked.</param>
[ViewNode("checkBoxView")]
public sealed record CheckBoxView(string Key, bool IsOn) : Node;
