using DigitalBrain;

namespace IntoChat.Muncly;

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
