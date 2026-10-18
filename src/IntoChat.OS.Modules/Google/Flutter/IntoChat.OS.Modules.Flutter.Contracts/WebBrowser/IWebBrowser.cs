using System.Text.Json.Serialization;
using IntoChat.OS;
using Orleans.Concurrency;

namespace IntoChat.OS.Flutter.WebBrowser;

[Alias("webbrowser"), Orleans.Metadata.DefaultGrainType(UIVocabulary.WebBrowserType)]
public interface IWebBrowser : INeuron
{
    Task Navigate(string uri, string? title = null);
    Task Configure(string playwrightKey);
    [ReadOnly, Alias("read")] Task<WebBrowserView> Read();
}

// The shell reads the connection by name.
[JsonConverter(typeof(JsonStringEnumConverter<BrowserConnection>))]
[GenerateSerializer, Alias("ui.browser-connection")]
public enum BrowserConnection { Waiting, Attached, Unavailable, Failed }

[GenerateSerializer, Alias("ui.webbrowser-state")]
public sealed class WebBrowserState
{
    [Id(0)] public string Name { get; set; } = "";
    [Id(1)] public int Version { get; set; }
    [Id(2)] public string Uri { get; set; } = "";
    [Id(3)] public string Title { get; set; } = "";
    [Id(4)] public string? PlaywrightKey { get; set; }
}

[GenerateSerializer, Alias("ui.webbrowser-view")]
public sealed class WebBrowserView
{
    [Id(0)] public string Name { get; set; } = "";
    [Id(1)] public int Version { get; set; }
    [Id(2)] public string Uri { get; set; } = "";
    [Id(3)] public string Title { get; set; } = "";
    [Id(4)] public BrowserConnection Connection { get; set; }
    [Id(5)] public string Error { get; set; } = "";
    [Id(6)] public string ActualUrl { get; set; } = "";
}

[GenerateSerializer, Alias("ui.browser-observation")]
public sealed record BrowserObservationChanged([property: Id(0)] string Name) : Signal;
