namespace DigitalBrain.UI.Controls;

/// <summary>The state of a toggle switch neuron (WinUI's ToggleSwitch): only what the user changes.</summary>
public sealed record ToggleSwitchState
{
    /// <summary>True when on.</summary>
    [Showcase("""{"kind":"toggleSwitchView","key":"dark-mode","isOn":true,"props":{"label":"Dark mode","onContent":"On","offContent":"Off","isEnabled":true,"automationName":"Dark mode"}}""")]
    [Showcase("""{"kind":"toggleSwitchView","key":"wifi","isOn":false,"props":{"label":"Wi-Fi","isEnabled":false,"automationName":"Wi-Fi"}}""")]
    public bool IsOn { get; init; }
}

/// <summary>What a toggle switch neuron draws: its key and value. The client draws the label, contents and enabled from the slot's props.</summary>
/// <param name="Key">The neuron's key, by which the client finds the control.</param>
/// <param name="IsOn">True when on.</param>
[ViewNode("toggleSwitchView")]
public sealed record ToggleSwitchView(string Key, bool IsOn) : Node;
