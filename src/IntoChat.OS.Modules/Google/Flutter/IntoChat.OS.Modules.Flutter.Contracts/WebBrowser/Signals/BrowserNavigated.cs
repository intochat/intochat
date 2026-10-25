using IntoChat.OS;

namespace IntoChat.OS.Modules.Flutter.WebBrowser.Signals;

[GenerateSerializer, Alias("ui.browser-navigated")]
public sealed record BrowserNavigated([property: Id(0)] string Name, [property: Id(1)] string Uri) : Signal;
