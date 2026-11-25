namespace DigitalBrain.UI.Controls;

/// <summary>The state of a slider neuron: only what the user changes. Its label and range are the parent's props.</summary>
public sealed record SliderControlState
{
    /// <summary>The value; null draws the props' minimum. Stays null while the parent owns the value.</summary>
    [Showcase("""{"kind":"sliderView","key":"volume","value":40,"props":{"label":"Volume","min":0,"max":100,"step":5,"unit":"%","isEnabled":true,"automationName":"Volume"}}""")]
    [Showcase("""{"kind":"sliderView","key":"locked","value":null,"props":{"label":"Temperature","min":0,"max":2,"step":0.1,"unit":"","isEnabled":false,"automationName":"Temperature","value":0.7}}""")]
    public double? Value { get; init; }
}

/// <summary>What a slider neuron draws: its key and value. The client draws the label, range and unit from the child's props.</summary>
/// <param name="Key">The neuron's key, by which the client finds the control.</param>
/// <param name="Value">The slider's own value; null when it has none yet or the parent owns the value.</param>
[ViewNode("sliderView")]
public sealed record SliderView(string Key, double? Value) : Node;
