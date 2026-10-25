using IntoChat.OS;

namespace IntoChat.OS.Modules.Flutter.WebBrowser.Signals;

[GenerateSerializer, Alias("ui.browser-disconnected")]
public sealed record BrowserDisconnected([property: Id(0)] string Name, [property: Id(1)] string SessionId) : Signal;
