using DigitalBrain.Contracts;
using DigitalBrain;
using IntoChat.OS;
using DigitalBrain.State;

namespace IntoChat.OS.Modules.Flutter.Button;

[NeuronAlias("button"), DefaultNeuronType(UIVocabulary.ButtonType)]
public interface IButton
{
    Task Set(string label, string action, bool enabled = true);
    Task SetVisual(ControlVisual visual);
    Task Click();
    Task ClickCurrent(string expectedAction);
    Task SetActivation(string label, string activationJson);
    [ReadOnlyCall] Task<ButtonState> Read();
}

[GenerateSerializer, Alias("ui.button-state")]
[ExportableState]
public sealed class ButtonState
{
    [Id(0)] public string Name { get; set; } = "";
    [Id(1)] public int Version { get; set; }
    [Id(2)] public string Label { get; set; } = "";
    [Id(3)] public string Action { get; set; } = "";
    [Id(4)] public bool Enabled { get; set; } = true;
    [Id(5)] public int ClickCount { get; set; }
    [Id(6)] public string? Activation { get; set; }
    [Id(7)] public ControlVisual? Visual { get; set; }
}

[GenerateSerializer, Alias("ui.control-visual")]
public sealed record ControlVisual(
    [property: Id(0)] string? Icon = null,
    [property: Id(1)] string? Svg = null,
    [property: Id(2)] string? ImageUrl = null,
    [property: Id(3)] bool Compact = false,
    [property: Id(4)] bool Selected = false,
    [property: Id(5)] bool Running = false,
    [property: Id(6)] bool Avatar = false,
    [property: Id(7)] string Tone = "neutral");
