using DigitalBrain;

namespace IntoChat.Muncly;

[Signal("muncly.open")]
public sealed record MunclyOpen(
    string? Path = null,
    string? ObjectName = null,
    string? RecordId = null,
    string? BaseUrl = null,
    string? Input = null) : Signal;

[Signal("muncly.connect")]
public sealed record MunclyConnect(string? BaseUrl = null) : Signal;

[Signal("muncly.reload")]
public sealed record MunclyReload : Signal;
