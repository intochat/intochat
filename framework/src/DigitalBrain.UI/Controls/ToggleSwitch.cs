namespace DigitalBrain.UI.Controls;

/// <summary>The state of a toggle switch neuron (WinUI's ToggleSwitch; the legacy <see cref="Switch"/> data node is the old way).</summary>
public sealed record ToggleSwitchState
{
    /// <summary>The header beside the switch.</summary>
    [Showcase("""{"kind":"toggleSwitchView","key":"dark-mode","label":"Dark mode","isOn":true,"onContent":"On","offContent":"Off","automationName":"Dark mode"}""")]
    [Showcase("""{"kind":"toggleSwitchView","key":"wifi","label":"Wi-Fi","isOn":false,"automationName":"Wi-Fi"}""")]
    public string Label { get; init; } = "";

    /// <summary>True when on.</summary>
    public bool IsOn { get; init; }

    /// <summary>The text shown while on; null shows none.</summary>
    public string? OnContent { get; init; }

    /// <summary>The text shown while off; null shows none.</summary>
    public string? OffContent { get; init; }

    /// <summary>The accessible name; null falls back to the neuron's key.</summary>
    public string? AutomationName { get; init; }
}

/// <summary>What a toggle switch neuron draws: plain data; the client sends its commands to the neuron (".").</summary>
/// <param name="Key">The neuron's key, by which the client finds the control.</param>
/// <param name="Label">The header beside the switch.</param>
/// <param name="IsOn">True when on.</param>
/// <param name="AutomationName">The accessible name.</param>
/// <param name="OnContent">The text shown while on.</param>
/// <param name="OffContent">The text shown while off.</param>
[ViewNode("toggleSwitchView")]
public sealed record ToggleSwitchView(string Key, string Label, bool IsOn, string AutomationName, string? OnContent = null, string? OffContent = null) : Node;
