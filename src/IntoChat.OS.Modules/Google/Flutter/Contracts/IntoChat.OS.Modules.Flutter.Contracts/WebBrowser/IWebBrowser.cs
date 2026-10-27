using DigitalBrain.Contracts;
using DigitalBrain;
using System.Text.Json.Serialization;
using IntoChat.OS;
using DigitalBrain.State;

namespace IntoChat.OS.Modules.Flutter.WebBrowser;

[NeuronAlias("webbrowser"), DefaultNeuronType(UIVocabulary.WebBrowserType)]
public interface IWebBrowser
{
    Task Navigate(string uri, string? title = null);
    Task Configure(string playwrightKey);
    [DigitalBrain.ReadOnlyCall] Task<WebBrowserView> Read();
}

// The shell reads the connection by name.
[JsonConverter(typeof(JsonStringEnumConverter<BrowserConnection>))]
[GenerateSerializer, Alias("ui.browser-connection")]
public enum BrowserConnection { Waiting, Attached, Unavailable, Failed }

[GenerateSerializer, Alias("ui.webbrowser-state")]
[ExportableState]
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

[Signal("ui.browser-observation")]
public sealed record BrowserObservationChanged(string Name) : DigitalBrain.Signal;
