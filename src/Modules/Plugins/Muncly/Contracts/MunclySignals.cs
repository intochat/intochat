using DigitalBrain;
using DigitalBrain.Contracts;
using DigitalBrain.UI;

namespace IntoChat.Muncly;

[DefaultNeuronType("control.form")]
internal interface IMunclyFormControl : IScreen, IControl<FormProps>;

[Signal("muncly.open")]
public sealed record MunclyOpen(
    string? Path = null,
    string? ObjectName = null,
    string? RecordId = null,
    string? BaseUrl = null,
    string? Input = null) : Signal;

// The URL field sends it with the typed URL as Value; a caller may name BaseUrl instead.
[Signal("muncly.connect")]
public sealed record MunclyConnect(string? BaseUrl = null, string? Value = null) : Signal;

[Signal("muncly.reload")]
public sealed record MunclyReload : Signal;

[Signal("muncly.probe")]
public sealed record MunclyProbe : Signal;

[Signal("muncly.disconnect")]
public sealed record MunclyDisconnect : Signal;

[Signal("muncly.toggle-inspector")]
public sealed record MunclyToggleInspector : Signal;
